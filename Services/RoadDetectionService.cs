using System.Diagnostics;
using Microsoft.Extensions.Options;
using RoadDefectDetection.Configuration;
using RoadDefectDetection.DTOs;
using RoadDefectDetection.Services.Interfaces;

namespace RoadDefectDetection.Services
{
    /// <summary>
    /// Orchestrates road defect detection across all loaded ONNX models.
    /// Models are loaded once at startup and reused for the application lifetime.
    /// </summary>
    public sealed class RoadDetectionService : IDetectionService, IDisposable
    {
        private readonly List<YoloDetector> _detectors = new();
        private readonly ILogger<RoadDetectionService> _logger;
        private readonly DetectionSettings _settings;
        private bool _disposed;

        public RoadDetectionService(
            IConfiguration configuration,
            IOptions<DetectionSettings> settings,
            ILogger<RoadDetectionService> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _settings = settings?.Value ?? throw new ArgumentNullException(nameof(settings));

            _logger.LogInformation(
                "Detection settings — Confidence: {C}, IoU: {I}, InputSize: {S}, MaxImageMB: {M}, MaxConcurrent: {P}",
                _settings.DefaultConfidenceThreshold,
                _settings.IouThreshold,
                _settings.InputSize,
                _settings.MaxImageSizeMB,
                _settings.MaxConcurrentDetections);

            var modelConfigs = new List<ModelConfig>();
            configuration.GetSection("Models").Bind(modelConfigs);

            if (modelConfigs.Count == 0)
            {
                _logger.LogWarning("No model configurations found in appsettings.json 'Models' section.");
                return;
            }

            _logger.LogInformation("Found {Count} model configuration(s). Loading enabled models...", modelConfigs.Count);

            // Validate ModelId uniqueness
            var duplicateIds = modelConfigs
                .Where(m => m.Enabled)
                .GroupBy(m => m.ModelId)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToList();

            if (duplicateIds.Count > 0)
                _logger.LogError("Duplicate ModelId(s) found: [{Ids}]. Each model must have a unique ModelId.",
                    string.Join(", ", duplicateIds));

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
                    _logger.LogInformation("Model '{Name}' (ID: {Id}) is disabled. Skipping.", config.Name, config.ModelId);
                    continue;
                }

                string modelPath = Path.Combine(modelsDirectory, config.FileName);
                if (!File.Exists(modelPath))
                {
                    _logger.LogWarning(
                        "Model file not found: '{Path}'. Skipping model '{Name}' (ID: {Id}).",
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
                        "Loaded model '{Name}' (ID: {Id}, File: {File}) with {Count} classes: [{Classes}].",
                        config.Name, config.ModelId, config.FileName,
                        config.Classes.Length, string.Join(", ", config.Classes));
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Failed to load model '{Name}' (ID: {Id}) from '{File}'.",
                        config.Name, config.ModelId, config.FileName);
                }
            }

            _logger.LogInformation(
                "Detection service initialized. {Loaded} of {Total} model(s) loaded.",
                _detectors.Count, modelConfigs.Count(m => m.Enabled));
        }

        /// <inheritdoc />
        public async Task<DetectionResponse> DetectAsync(
            byte[] imageBytes,
            string imageName,
            float? confidenceThreshold = null,
            CancellationToken cancellationToken = default)
        {
            var sw = Stopwatch.StartNew();

            if (_detectors.Count == 0)
            {
                _logger.LogWarning("DetectAsync called but no models are loaded.");
                return new DetectionResponse
                {
                    Success = false,
                    Message = "No detection models are currently loaded.",
                    ImageName = imageName,
                    ProcessingTimeMs = sw.Elapsed.TotalMilliseconds
                };
            }

            try
            {
                float? normalizedConf = NormalizeConfidence(confidenceThreshold);

                _logger.LogInformation(
                    "Detecting on '{Name}' ({Size} bytes), {Models} model(s), confidence: {Conf}",
                    imageName, imageBytes.Length, _detectors.Count,
                    normalizedConf?.ToString("P0") ?? "default");

                var tasks = _detectors.Select(detector =>
                    Task.Run(() => detector.Detect(imageBytes, normalizedConf), cancellationToken)
                ).ToArray();

                var allResults = await Task.WhenAll(tasks);

                var combined = allResults
                    .SelectMany(r => r)
                    .OrderByDescending(d => d.Confidence)
                    .ToList();

                sw.Stop();

                _logger.LogInformation(
                    "Detection on '{Name}': {Count} defect(s) in {Time:F1}ms.",
                    imageName, combined.Count, sw.Elapsed.TotalMilliseconds);

                return new DetectionResponse
                {
                    Success = true,
                    Message = combined.Count > 0
                        ? $"Detection completed. Found {combined.Count} potential defect(s)."
                        : "Detection completed. No defects found.",
                    ImageName = imageName,
                    TotalProblemsFound = combined.Count,
                    ProcessingTimeMs = Math.Round(sw.Elapsed.TotalMilliseconds, 1),
                    Detections = combined
                };
            }
            catch (OperationCanceledException)
            {
                sw.Stop();
                _logger.LogInformation("Detection cancelled for '{Name}'.", imageName);
                return new DetectionResponse
                {
                    Success = false,
                    Message = "Detection was cancelled.",
                    ImageName = imageName,
                    ProcessingTimeMs = Math.Round(sw.Elapsed.TotalMilliseconds, 1)
                };
            }
            catch (Exception ex)
            {
                sw.Stop();
                _logger.LogError(ex, "Error during detection on '{Name}'.", imageName);
                return new DetectionResponse
                {
                    Success = false,
                    Message = "Detection failed. See server logs for details.",
                    ImageName = imageName,
                    ProcessingTimeMs = Math.Round(sw.Elapsed.TotalMilliseconds, 1)
                };
            }
        }

        /// <inheritdoc />
        public async Task<List<DetectionResponse>> DetectMultipleAsync(
            List<(byte[] bytes, string name)> images,
            float? confidenceThreshold = null,
            CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("Batch detection on {Count} image(s).", images.Count);

            int maxConcurrent = Math.Max(1, _settings.MaxConcurrentDetections);
            var semaphore = new SemaphoreSlim(maxConcurrent, maxConcurrent);
            var responses = new DetectionResponse[images.Count];

            var tasks = images.Select(async (item, index) =>
            {
                await semaphore.WaitAsync(cancellationToken);
                try
                {
                    responses[index] = await DetectAsync(
                        item.bytes, item.name, confidenceThreshold, cancellationToken);
                }
                finally
                {
                    semaphore.Release();
                }
            });

            await Task.WhenAll(tasks);

            int total = responses.Sum(r => r.TotalProblemsFound);
            _logger.LogInformation(
                "Batch complete. {Images} image(s), {Defects} total defect(s).",
                images.Count, total);

            return responses.ToList();
        }

        /// <inheritdoc />
        public List<ModelLoadedInfo> GetLoadedModels()
        {
            return _detectors.Select(d => new ModelLoadedInfo
            {
                Name = d.ModelName,
                ModelId = d.ModelId,
                Classes = d.ClassNames,
                ClassCount = d.ClassNames.Length,
                Status = "Loaded"
            }).ToList();
        }

        /// <inheritdoc />
        public bool IsHealthy() => _detectors.Count > 0;

        public void Dispose()
        {
            if (_disposed) return;
            _logger.LogInformation(
                "Disposing detection service ({Count} model(s)).", _detectors.Count);
            foreach (var d in _detectors)
            {
                try { d.Dispose(); }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error disposing detector '{Name}'.", d.ModelName);
                }
            }
            _detectors.Clear();
            _disposed = true;
        }

        // ── Helpers ─────────────────────────────────────────────

        private static float? NormalizeConfidence(float? raw)
        {
            if (!raw.HasValue) return null;
            float v = raw.Value > 1.0f ? raw.Value / 100f : raw.Value;
            return Math.Clamp(v, 0.01f, 1.0f);
        }
    }
}