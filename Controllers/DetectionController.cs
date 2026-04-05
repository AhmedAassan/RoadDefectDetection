using Microsoft.AspNetCore.Mvc;
using RoadDefectDetection.Configuration;
using RoadDefectDetection.DTOs;
using RoadDefectDetection.Services.Interfaces;

namespace RoadDefectDetection.Controllers
{
    /// <summary>
    /// API controller for road defect detection operations.
    /// Provides endpoints for single-image detection, batch detection,
    /// mapped detection (for external systems), model information, and health checks.
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    public class DetectionController : ControllerBase
    {
        private readonly IDetectionService _service;
        private readonly IExternalMappingService _mappingService;
        private readonly ILogger<DetectionController> _logger;
        private readonly int _maxImageSizeMB;

        private static readonly string[] AllowedExtensions = { ".jpg", ".jpeg", ".png", ".bmp" };

        public DetectionController(
            IDetectionService service,
            IExternalMappingService mappingService,
            ILogger<DetectionController> logger,
            IConfiguration configuration)
        {
            _service = service;
            _mappingService = mappingService;
            _logger = logger;

            var settings = new DetectionSettings();
            configuration.GetSection("DetectionSettings").Bind(settings);
            _maxImageSizeMB = settings.MaxImageSizeMB > 0 ? settings.MaxImageSizeMB : 50;
        }

        // ═══════════════════════════════════════════════════════════
        // STANDARD DETECTION ENDPOINTS (unchanged behavior)
        // ═══════════════════════════════════════════════════════════

        /// <summary>
        /// Analyzes a single road image for defects.
        /// Returns internal detection results (model class names and indices).
        /// </summary>
        [HttpPost("detect")]
        [ProducesResponseType(typeof(DetectionResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
        public async Task<IActionResult> Detect(
            IFormFile image,
            [FromForm] float? confidence = null)
        {
            var validation = ValidateImage(image, confidence);
            if (validation != null) return validation;

            byte[] imageBytes;
            using (var ms = new MemoryStream())
            {
                await image.CopyToAsync(ms);
                imageBytes = ms.ToArray();
            }

            _logger.LogInformation("Processing '{FileName}' ({Size} bytes), confidence: {Conf}%.",
                image.FileName, image.Length, confidence?.ToString() ?? "default");

            var response = await _service.DetectAsync(imageBytes, image.FileName, confidence);
            return Ok(response);
        }

        /// <summary>
        /// Analyzes multiple road images for defects in a single request.
        /// </summary>
        [HttpPost("detect-multiple")]
        [ProducesResponseType(typeof(List<DetectionResponse>), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
        public async Task<IActionResult> DetectMultiple(
            List<IFormFile> images,
            [FromForm] float? confidence = null)
        {
            if (!_service.IsHealthy())
                return StatusCode(503, new { success = false, message = "No detection models are currently loaded." });

            if (confidence.HasValue && (confidence.Value < 0 || confidence.Value > 100))
                return BadRequest(new { success = false, message = "Confidence must be between 0 and 100." });

            if (images == null || images.Count == 0)
                return BadRequest(new { success = false, message = "No image files provided." });

            long maxSizeBytes = (long)_maxImageSizeMB * 1024 * 1024;

            for (int i = 0; i < images.Count; i++)
            {
                var file = images[i];
                if (file == null || file.Length == 0)
                    return BadRequest(new { success = false, message = $"File at index {i} is empty." });

                string ext = Path.GetExtension(file.FileName).ToLowerInvariant();
                if (!AllowedExtensions.Contains(ext))
                    return BadRequest(new { success = false, message = $"File '{file.FileName}' has invalid format." });

                if (file.Length > maxSizeBytes)
                    return BadRequest(new { success = false, message = $"File '{file.FileName}' exceeds size limit." });
            }

            var imageDataList = new List<(byte[] bytes, string name)>(images.Count);
            foreach (var file in images)
            {
                using var ms = new MemoryStream();
                await file.CopyToAsync(ms);
                imageDataList.Add((ms.ToArray(), file.FileName));
            }

            var responses = await _service.DetectMultipleAsync(imageDataList, confidence);
            return Ok(responses);
        }

        // ═══════════════════════════════════════════════════════════
        // MAPPED DETECTION ENDPOINTS (for external system)
        // ═══════════════════════════════════════════════════════════

        /// <summary>
        /// Analyzes a single image and returns results mapped to the external system's
        /// class IDs and names. Use this endpoint when forwarding results to the
        /// external UI system.
        /// 
        /// Each detection includes:
        /// - Internal data: model class name, class index, model ID
        /// - External data: external class ID, external class name, severity, category
        /// </summary>
        /// <param name="image">The image file to analyze.</param>
        /// <param name="confidence">Optional confidence threshold (0-100).</param>
        /// <returns>
        /// 200 OK with <see cref="MappedDetectionResponse"/> containing mapped detections.
        /// </returns>
        [HttpPost("detect-mapped")]
        [ProducesResponseType(typeof(MappedDetectionResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
        public async Task<IActionResult> DetectMapped(
            IFormFile image,
            [FromForm] float? confidence = null)
        {
            var validation = ValidateImage(image, confidence);
            if (validation != null) return validation;

            byte[] imageBytes;
            using (var ms = new MemoryStream())
            {
                await image.CopyToAsync(ms);
                imageBytes = ms.ToArray();
            }

            _logger.LogInformation(
                "Processing mapped detection for '{FileName}' ({Size} bytes).",
                image.FileName, image.Length);

            var response = await _service.DetectAsync(imageBytes, image.FileName, confidence);
            var mappedResponse = _mappingService.MapResponse(response);

            return Ok(mappedResponse);
        }

        /// <summary>
        /// Analyzes multiple images and returns results mapped to the external system.
        /// </summary>
        [HttpPost("detect-mapped-multiple")]
        [ProducesResponseType(typeof(List<MappedDetectionResponse>), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
        public async Task<IActionResult> DetectMappedMultiple(
            List<IFormFile> images,
            [FromForm] float? confidence = null)
        {
            if (!_service.IsHealthy())
                return StatusCode(503, new { success = false, message = "No detection models are currently loaded." });

            if (confidence.HasValue && (confidence.Value < 0 || confidence.Value > 100))
                return BadRequest(new { success = false, message = "Confidence must be between 0 and 100." });

            if (images == null || images.Count == 0)
                return BadRequest(new { success = false, message = "No image files provided." });

            long maxSizeBytes = (long)_maxImageSizeMB * 1024 * 1024;

            for (int i = 0; i < images.Count; i++)
            {
                var file = images[i];
                if (file == null || file.Length == 0)
                    return BadRequest(new { success = false, message = $"File at index {i} is empty." });

                string ext = Path.GetExtension(file.FileName).ToLowerInvariant();
                if (!AllowedExtensions.Contains(ext))
                    return BadRequest(new { success = false, message = $"File '{file.FileName}' has invalid format." });

                if (file.Length > maxSizeBytes)
                    return BadRequest(new { success = false, message = $"File '{file.FileName}' exceeds size limit." });
            }

            var imageDataList = new List<(byte[] bytes, string name)>(images.Count);
            foreach (var file in images)
            {
                using var ms = new MemoryStream();
                await file.CopyToAsync(ms);
                imageDataList.Add((ms.ToArray(), file.FileName));
            }

            var responses = await _service.DetectMultipleAsync(imageDataList, confidence);
            var mappedResponses = responses.Select(r => _mappingService.MapResponse(r)).ToList();

            return Ok(mappedResponses);
        }

        // ═══════════════════════════════════════════════════════════
        // MAPPING MANAGEMENT ENDPOINTS
        // ═══════════════════════════════════════════════════════════

        /// <summary>
        /// Returns all external class mappings configured in the system.
        /// Shows how internal model classes map to external system IDs.
        /// </summary>
        [HttpGet("mappings")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public IActionResult GetMappings()
        {
            var mappings = _mappingService.GetAllMappings();
            var models = _service.GetLoadedModels();

            return Ok(new
            {
                totalMappings = mappings.Count,
                modelsWithMappings = mappings.Select(m => m.ModelId).Distinct().Count(),
                mappings,
                loadedModels = models
            });
        }

        /// <summary>
        /// Returns external class mappings for a specific model.
        /// </summary>
        /// <param name="modelId">The model ID to get mappings for.</param>
        [HttpGet("mappings/model/{modelId}")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public IActionResult GetMappingsForModel(int modelId)
        {
            var mappings = _mappingService.GetMappingsForModel(modelId);

            return Ok(new
            {
                modelId,
                totalMappings = mappings.Count,
                mappings
            });
        }

        // ═══════════════════════════════════════════════════════════
        // INFO & HEALTH ENDPOINTS
        // ═══════════════════════════════════════════════════════════

        /// <summary>
        /// Returns information about all currently loaded detection models.
        /// </summary>
        [HttpGet("models")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public IActionResult GetModels()
        {
            var models = _service.GetLoadedModels();
            return Ok(new { totalModels = models.Count, models });
        }

        /// <summary>
        /// Health check endpoint.
        /// </summary>
        [HttpGet("health")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public IActionResult Health()
        {
            bool isHealthy = _service.IsHealthy();
            var models = _service.GetLoadedModels();
            var mappings = _mappingService.GetAllMappings();

            return Ok(new
            {
                status = isHealthy ? "Healthy" : "Unhealthy",
                isHealthy,
                loadedModels = models.Count,
                configuredMappings = mappings.Count,
                message = isHealthy
                    ? $"Service is operational with {models.Count} model(s) and {mappings.Count} mapping(s)."
                    : "No detection models are loaded. Place .onnx files in the Models folder and restart."
            });
        }

        // ═══════════════════════════════════════════════════════════
        // PRIVATE HELPERS
        // ═══════════════════════════════════════════════════════════

        /// <summary>
        /// Validates common image upload parameters.
        /// Returns an IActionResult if validation fails, null if OK.
        /// </summary>
        private IActionResult? ValidateImage(IFormFile? image, float? confidence)
        {
            if (!_service.IsHealthy())
            {
                _logger.LogWarning("Detection request rejected: no models loaded.");
                return StatusCode(StatusCodes.Status503ServiceUnavailable,
                    new { success = false, message = "No detection models are currently loaded." });
            }

            if (confidence.HasValue && (confidence.Value < 0 || confidence.Value > 100))
            {
                return BadRequest(new { success = false, message = "Confidence must be between 0 and 100." });
            }

            if (image == null || image.Length == 0)
            {
                return BadRequest(new { success = false, message = "No image file provided." });
            }

            string extension = Path.GetExtension(image.FileName).ToLowerInvariant();
            if (!AllowedExtensions.Contains(extension))
            {
                return BadRequest(new
                {
                    success = false,
                    message = $"Invalid file format '{extension}'. Allowed: {string.Join(", ", AllowedExtensions)}"
                });
            }

            long maxSizeBytes = (long)_maxImageSizeMB * 1024 * 1024;
            if (image.Length > maxSizeBytes)
            {
                return BadRequest(new
                {
                    success = false,
                    message = $"Image size ({image.Length / (1024.0 * 1024.0):F1}MB) exceeds {_maxImageSizeMB}MB limit."
                });
            }

            return null; // Validation passed
        }
    }
}