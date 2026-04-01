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
    /// To add a new model: drop the .onnx file in the Models folder, add an entry
    /// to the "Models" array in appsettings.json, and restart. No code changes needed.
    /// </summary>
    public sealed class RoadDetectionService : IDetectionService, IDisposable
    {
        private readonly List<YoloDetector> _detectors = new();
        private readonly ILogger<RoadDetectionService> _logger;
        private readonly DetectionSettings _settings;
        private bool _disposed;

        /// <summary>
        /// Initializes the detection service by loading all enabled ONNX models.
        /// Models that cannot be found or fail to load are skipped with a warning — 
        /// the service will still start with whatever models loaded successfully.
        /// </summary>
        /// <param name="configuration">Application configuration containing DetectionSettings and Models sections.</param>
        /// <param name="logger">Logger instance for diagnostic output.</param>
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
            // 3. Load each enabled model
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
                    _logger.LogInformation("Model '{ModelName}' is disabled. Skipping.", config.Name);
                    continue;
                }

                string modelPath = Path.Combine(modelsDirectory, config.FileName);

                if (!File.Exists(modelPath))
                {
                    _logger.LogWarning(
                        "Model file not found: '{ModelPath}'. Skipping model '{ModelName}'. " +
                        "Place the .onnx file in the Models folder and restart.",
                        modelPath, config.Name);
                    continue;
                }

                try
                {
                    // Use model-specific threshold, fall back to global default
                    float threshold = config.ConfidenceThreshold > 0
                        ? config.ConfidenceThreshold
                        : _settings.DefaultConfidenceThreshold;

                    var detector = new YoloDetector(
                        onnxPath: modelPath,
                        classNames: config.Classes,
                        confidenceThreshold: threshold,
                        iouThreshold: _settings.IouThreshold,
                        modelName: config.Name,
                        inputSize: _settings.InputSize);

                    _detectors.Add(detector);

                    _logger.LogInformation(
                        "Successfully loaded model '{ModelName}' ({FileName}) with {ClassCount} classes: [{Classes}].",
                        config.Name,
                        config.FileName,
                        config.Classes.Length,
                        string.Join(", ", config.Classes));
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex,
                        "Failed to load model '{ModelName}' from '{FileName}'. This model will be unavailable.",
                        config.Name, config.FileName);
                }
            }

            _logger.LogInformation(
                "Detection service initialized. {Loaded} of {Total} model(s) loaded successfully.",
                _detectors.Count, modelConfigs.Count(m => m.Enabled));
        }

        /// <summary>
        /// Analyzes a single image for road defects using all loaded models in parallel.
        /// 
        /// Each model runs on a separate thread via Task.Run. Results from all models
        /// are merged into a single list sorted by confidence descending.
        /// </summary>
        /// <param name="imageBytes">Raw bytes of the image file.</param>
        /// <param name="imageName">Original file name for tracking in the response.</param>
        /// <returns>A <see cref="DetectionResponse"/> containing aggregated detections from all models.</returns>
        public async Task<DetectionResponse> DetectAsync(byte[] imageBytes, string imageName, float? confidenceThreshold = null)
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
                // If the user sold confidence as a percentage (0-100), convert it to (0-1)
                float? normalizedConfidence = null;
                if (confidenceThreshold.HasValue)
                {
                    // If the value is greater than 1, the user will still sell it as a percentage
                    if (confidenceThreshold.Value > 1.0f)
                    {
                        normalizedConfidence = confidenceThreshold.Value / 100f;
                    }
                    else
                    {
                        normalizedConfidence = confidenceThreshold.Value;
                    }

                    // Clamp between 0 and 1
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

        /// <summary>
        /// Analyzes multiple images sequentially. Each image is processed with all
        /// models in parallel (via <see cref="DetectAsync"/>), but images are handled
        /// one at a time to prevent excessive resource consumption.
        /// </summary>
        /// <param name="images">List of tuples containing image bytes and file names.</param>
        /// <returns>A list of <see cref="DetectionResponse"/>, one per input image, in the same order.</returns>
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

        /// <summary>
        /// Returns metadata about all currently loaded models.
        /// Useful for admin dashboards and health-check UIs.
        /// </summary>
        /// <returns>A list of objects with Name, Classes, ClassCount, and Status properties.</returns>
        public List<object> GetLoadedModels()
        {
            return _detectors.Select(d => (object)new
            {
                Name = d.ModelName,
                Classes = d.ClassNames,
                ClassCount = d.ClassNames.Length,
                Status = "Loaded"
            }).ToList();
        }

        /// <summary>
        /// Returns true if at least one ONNX model is loaded and ready for inference.
        /// </summary>
        /// <returns>True if operational; false if no models could be loaded.</returns>
        public bool IsHealthy()
        {
            return _detectors.Count > 0;
        }

        /// <summary>
        /// Disposes all <see cref="YoloDetector"/> instances and their underlying ONNX sessions.
        /// Called automatically when the DI container is disposed at shutdown.
        /// </summary>
        public void Dispose()
        {
            if (!_disposed)
            {
                _logger.LogInformation("Disposing detection service and releasing {Count} model(s).", _detectors.Count);

                foreach (var detector in _detectors)
                {
                    try
                    {
                        detector.Dispose();
                    }
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