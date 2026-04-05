using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using RoadDefectDetection.Configuration;
using RoadDefectDetection.DTOs;
using RoadDefectDetection.Services.Interfaces;

namespace RoadDefectDetection.Services
{
    /// <summary>
    /// Orchestrates road defect detection across all loaded ONNX models.
    /// 
    /// At startup, reads model configurations from appsettings.json, loads each
    /// enabled ONNX model into a <see cref="YoloDetector"/> instance, and holds
    /// them for the lifetime of the application. When a detection request arrives,
    /// all models run in parallel against the same image, and results are merged.
    /// 
    /// Now passes ModelId to each YoloDetector so detections carry the model
    /// identifier needed for external system mapping.
    /// </summary>
    public sealed class RoadDetectionService : IDetectionService, IDisposable
    {
        private readonly List<YoloDetector> _detectors = new();
        private readonly ILogger<RoadDetectionService> _logger;
        private readonly DetectionSettings _settings;
        private bool _disposed;

        public RoadDetectionService(IConfiguration configuration, ILogger<RoadDetectionService> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            // ------------------------------------------------------------------
            // 1. Bind global detection settings
            // ------------------------------------------------------------------
            _settings = new DetectionSettings();
            configuration.GetSection("DetectionSettings").Bind(_settings);

            _logger.LogInformation(
                "Detection settings loaded — DefaultConfidence: {Confidence}, IoU: {IoU}, InputSize: {Size}, MaxImageSize: {Max}MB.",
                _settings.DefaultConfidenceThreshold,
                _settings.IouThreshold,
                _settings.InputSize,
                _settings.MaxImageSizeMB);

            // ------------------------------------------------------------------
            // 2. Bind model configurations
            // ------------------------------------------------------------------
            var modelConfigs = new List<ModelConfig>();
            configuration.GetSection("Models").Bind(modelConfigs);

            if (modelConfigs.Count == 0)
            {
                _logger.LogWarning("No model configurations found in appsettings.json 'Models' section.");
                return;
            }

            _logger.LogInformation("Found {Count} model configuration(s). Loading enabled models...", modelConfigs.Count);

            // ------------------------------------------------------------------
            // 3. Validate ModelId uniqueness
            // ------------------------------------------------------------------
            var duplicateIds = modelConfigs
                .Where(m => m.Enabled)
                .GroupBy(m => m.ModelId)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToList();

            if (duplicateIds.Count > 0)
            {
                _logger.LogError(
                    "Duplicate ModelId(s) found: [{Ids}]. Each model must have a unique ModelId.",
                    string.Join(", ", duplicateIds));
            }

            // ------------------------------------------------------------------
            // 4. Load each enabled model
            // ------------------------------------------------------------------
            string modelsDirectory = Path.Combine(Directory.GetCurrentDirectory(), "Models");

            if (!Directory.Exists(modelsDirectory))
            {
                _logger.LogWarning("Models directory not found at '{Path}'. Creating it.", modelsDirectory);
                Directory.CreateDirectory(modelsDirectory);
            }

            foreach (var config in modelConfigs)
            {
                if (!config.Enabled)
                {
                    _logger.LogInformation("Model '{ModelName}' (ID: {ModelId}) is disabled. Skipping.",
                        config.Name, config.ModelId);
                    continue;
                }

                string modelPath = Path.Combine(modelsDirectory, config.FileName);

                if (!File.Exists(modelPath))
                {
                    _logger.LogWarning(
                        "Model file not found: '{ModelPath}'. Skipping model '{ModelName}' (ID: {ModelId}). " +
                        "Place the .onnx file in the Models folder and restart.",
                        modelPath, config.Name, config.ModelId);
                    continue;
                }

                try
                {
                    float threshold = config.ConfidenceThreshold > 0
                        ? config.ConfidenceThreshold
                        : _settings.DefaultConfidenceThreshold;

                    var detector = new YoloDetector(
                        onnxPath: modelPath,
                        classNames: config.Classes,
                        confidenceThreshold: threshold,
                        iouThreshold: _settings.IouThreshold,
                        modelName: config.Name,
                        modelId: config.ModelId,
                        inputSize: _settings.InputSize);

                    _detectors.Add(detector);

                    _logger.LogInformation(
                        "Successfully loaded model '{ModelName}' (ID: {ModelId}, File: {FileName}) " +
                        "with {ClassCount} classes: [{Classes}].",
                        config.Name,
                        config.ModelId,
                        config.FileName,
                        config.Classes.Length,
                        string.Join(", ", config.Classes));
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex,
                        "Failed to load model '{ModelName}' (ID: {ModelId}) from '{FileName}'. " +
                        "This model will be unavailable.",
                        config.Name, config.ModelId, config.FileName);
                }
            }

            _logger.LogInformation(
                "Detection service initialized. {Loaded} of {Total} model(s) loaded successfully.",
                _detectors.Count, modelConfigs.Count(m => m.Enabled));
        }

        /// <inheritdoc />
        public async Task<DetectionResponse> DetectAsync(
            byte[] imageBytes, string imageName, float? confidenceThreshold = null)
        {
            var stopwatch = Stopwatch.StartNew();

            if (_detectors.Count == 0)
            {
                _logger.LogWarning("DetectAsync called but no models are loaded.");
                return new DetectionResponse
                {
                    Success = false,
                    Message = "No detection models are currently loaded.",
                    ImageName = imageName,
                    TotalProblemsFound = 0,
                    ProcessingTimeMs = stopwatch.Elapsed.TotalMilliseconds,
                    Detections = new List<DetectionResult>()
                };
            }

            try
            {
                float? normalizedConfidence = null;
                if (confidenceThreshold.HasValue)
                {
                    normalizedConfidence = confidenceThreshold.Value > 1.0f
                        ? confidenceThreshold.Value / 100f
                        : confidenceThreshold.Value;

                    normalizedConfidence = Math.Clamp(normalizedConfidence.Value, 0.01f, 1.0f);
                }

                _logger.LogInformation(
                    "Starting detection on '{ImageName}' ({Size} bytes) with {ModelCount} model(s). Confidence: {Confidence}",
                    imageName, imageBytes.Length, _detectors.Count,
                    normalizedConfidence?.ToString("P0") ?? "default");

                var tasks = _detectors.Select(detector =>
                    Task.Run(() => detector.Detect(imageBytes, normalizedConfidence))
                ).ToArray();

                var allResults = await Task.WhenAll(tasks);

                var combinedDetections = allResults
                    .SelectMany(results => results)
                    .OrderByDescending(d => d.Confidence)
                    .ToList();

                stopwatch.Stop();

                _logger.LogInformation(
                    "Detection complete on '{ImageName}': {Count} defect(s) found in {Time:F1}ms.",
                    imageName, combinedDetections.Count, stopwatch.Elapsed.TotalMilliseconds);

                return new DetectionResponse
                {
                    Success = true,
                    Message = combinedDetections.Count > 0
                        ? $"Detection completed. Found {combinedDetections.Count} potential defect(s)."
                        : "Detection completed. No defects found.",
                    ImageName = imageName,
                    TotalProblemsFound = combinedDetections.Count,
                    ProcessingTimeMs = Math.Round(stopwatch.Elapsed.TotalMilliseconds, 1),
                    Detections = combinedDetections
                };
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                _logger.LogError(ex, "Error during detection on '{ImageName}'.", imageName);

                return new DetectionResponse
                {
                    Success = false,
                    Message = $"Detection failed: {ex.Message}",
                    ImageName = imageName,
                    TotalProblemsFound = 0,
                    ProcessingTimeMs = Math.Round(stopwatch.Elapsed.TotalMilliseconds, 1),
                    Detections = new List<DetectionResult>()
                };
            }
        }

        /// <inheritdoc />
        public async Task<List<DetectionResponse>> DetectMultipleAsync(
            List<(byte[] bytes, string name)> images, float? confidenceThreshold = null)
        {
            _logger.LogInformation("Starting batch detection on {Count} image(s).", images.Count);

            var responses = new List<DetectionResponse>(images.Count);

            foreach (var (bytes, name) in images)
            {
                var response = await DetectAsync(bytes, name, confidenceThreshold);
                responses.Add(response);
            }

            int totalDefects = responses.Sum(r => r.TotalProblemsFound);
            _logger.LogInformation(
                "Batch detection complete. Processed {ImageCount} image(s), found {DefectCount} total defect(s).",
                images.Count, totalDefects);

            return responses;
        }

        /// <inheritdoc />
        public List<object> GetLoadedModels()
        {
            return _detectors.Select(d => (object)new
            {
                Name = d.ModelName,
                d.ModelId,
                Classes = d.ClassNames,
                ClassCount = d.ClassNames.Length,
                Status = "Loaded"
            }).ToList();
        }

        /// <inheritdoc />
        public bool IsHealthy()
        {
            return _detectors.Count > 0;
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _logger.LogInformation("Disposing detection service and releasing {Count} model(s).", _detectors.Count);

                foreach (var detector in _detectors)
                {
                    try { detector.Dispose(); }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error disposing detector '{ModelName}'.", detector.ModelName);
                    }
                }

                _detectors.Clear();
                _disposed = true;
            }
        }
    }
}