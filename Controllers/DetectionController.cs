using Microsoft.AspNetCore.Mvc;
using RoadDefectDetection.Configuration;
using RoadDefectDetection.DTOs;
using RoadDefectDetection.Services.Interfaces;

namespace RoadDefectDetection.Controllers
{
    /// <summary>
    /// API controller for road defect detection operations.
    /// Provides endpoints for single-image detection, batch detection,
    /// model information, and health checks.
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    public class DetectionController : ControllerBase
    {
        private readonly IDetectionService _service;
        private readonly ILogger<DetectionController> _logger;
        private readonly int _maxImageSizeMB;

        /// <summary>
        /// Allowed image file extensions (case-insensitive check is applied).
        /// </summary>
        private static readonly string[] AllowedExtensions = { ".jpg", ".jpeg", ".png", ".bmp" };

        /// <summary>
        /// Initializes the controller with detection service, logger, and configuration.
        /// </summary>
        /// <param name="service">The detection service orchestrator.</param>
        /// <param name="logger">Logger for request-level diagnostics.</param>
        /// <param name="configuration">App configuration to read MaxImageSizeMB.</param>
        public DetectionController(
            IDetectionService service,
            ILogger<DetectionController> logger,
            IConfiguration configuration)
        {
            _service = service;
            _logger = logger;

            // Read max image size from settings, default to 50MB
            var settings = new DetectionSettings();
            configuration.GetSection("DetectionSettings").Bind(settings);
            _maxImageSizeMB = settings.MaxImageSizeMB > 0 ? settings.MaxImageSizeMB : 50;
        }

        /// <summary>
        /// Analyzes a single road image for defects.
        /// </summary>
        /// <param name="image">The image file to analyze (JPEG, PNG, or BMP).</param>
        /// <param name="confidence">Optional confidence threshold (0-100). Default uses model settings.</param>
        /// <returns>
        /// 200 OK with <see cref="DetectionResponse"/> containing all detected defects.
        /// 400 Bad Request if the file is missing, has an invalid format, or exceeds size limit.
        /// 503 Service Unavailable if no detection models are loaded.
        /// </returns>
        /// <remarks>
        /// Sample request:
        ///     POST /api/detection/detect
        ///     Content-Type: multipart/form-data
        ///     Form field: "image" = [file]
        /// </remarks>
        [HttpPost("detect")]
        [ProducesResponseType(typeof(DetectionResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
        public async Task<IActionResult> Detect(
        IFormFile image,
        [FromForm] float? confidence = null)
        {
            // --- Service availability check ---
            if (!_service.IsHealthy())
            {
                _logger.LogWarning("Detection request rejected: no models loaded.");
                return StatusCode(StatusCodes.Status503ServiceUnavailable,
                    new { success = false, message = "No detection models are currently loaded." });
            }

            // --- Confidence validation ---
            if (confidence.HasValue && (confidence.Value < 0 || confidence.Value > 100))
            {
                return BadRequest(new
                {
                    success = false,
                    message = "Confidence must be between 0 and 100."
                });
            }

            // --- File presence validation ---
            if (image == null || image.Length == 0)
            {
                return BadRequest(new { success = false, message = "No image file provided." });
            }

            // --- File extension validation ---
            string extension = Path.GetExtension(image.FileName).ToLowerInvariant();
            if (!AllowedExtensions.Contains(extension))
            {
                return BadRequest(new
                {
                    success = false,
                    message = $"Invalid file format '{extension}'. Allowed: {string.Join(", ", AllowedExtensions)}"
                });
            }

            // --- File size validation ---
            long maxSizeBytes = (long)_maxImageSizeMB * 1024 * 1024;
            if (image.Length > maxSizeBytes)
            {
                return BadRequest(new
                {
                    success = false,
                    message = $"Image size ({image.Length / (1024.0 * 1024.0):F1}MB) exceeds {_maxImageSizeMB}MB limit."
                });
            }

            _logger.LogInformation("Processing '{FileName}' ({Size} bytes), confidence: {Conf}%.",
                image.FileName, image.Length, confidence?.ToString() ?? "default");

            byte[] imageBytes;
            using (var ms = new MemoryStream())
            {
                await image.CopyToAsync(ms);
                imageBytes = ms.ToArray();
            }

            var response = await _service.DetectAsync(imageBytes, image.FileName, confidence);
            return Ok(response);
        }

        /// <summary>
        /// Analyzes multiple road images for defects in a single request.
        /// Each image is validated individually and processed sequentially.
        /// </summary>
        /// <param name="images">List of image files to analyze.</param>
        /// <param name="confidence">Optional confidence threshold (0-100).</param>
        /// <returns>
        /// 200 OK with a list of <see cref="DetectionResponse"/>, one per image.
        /// 400 Bad Request if no files are provided or any file fails validation.
        /// 503 Service Unavailable if no detection models are loaded.
        /// </returns>
        /// <remarks>
        /// Sample request:
        ///     POST /api/detection/detect-multiple
        ///     Content-Type: multipart/form-data
        ///     Form field: "images" = [file1, file2, ...]
        /// </remarks>
        [HttpPost("detect-multiple")]
        [ProducesResponseType(typeof(List<DetectionResponse>), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
        public async Task<IActionResult> DetectMultiple(
        List<IFormFile> images,
        [FromForm] float? confidence = null)
        {
            if (!_service.IsHealthy())
            {
                return StatusCode(StatusCodes.Status503ServiceUnavailable,
                    new { success = false, message = "No detection models are currently loaded." });
            }

            if (confidence.HasValue && (confidence.Value < 0 || confidence.Value > 100))
            {
                return BadRequest(new { success = false, message = "Confidence must be between 0 and 100." });
            }

            if (images == null || images.Count == 0)
            {
                return BadRequest(new { success = false, message = "No image files provided." });
            }

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

        /// <summary>
        /// Returns information about all currently loaded detection models,
        /// including their names, class labels, and status.
        /// </summary>
        /// <returns>200 OK with a list of model metadata objects.</returns>
        [HttpGet("models")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public IActionResult GetModels()
        {
            var models = _service.GetLoadedModels();

            return Ok(new
            {
                totalModels = models.Count,
                models
            });
        }

        /// <summary>
        /// Health check endpoint. Returns the operational status of the detection service
        /// and the number of loaded models.
        /// </summary>
        /// <returns>
        /// 200 OK with health status, even if unhealthy (so monitoring tools can read the body).
        /// </returns>
        [HttpGet("health")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public IActionResult Health()
        {
            bool isHealthy = _service.IsHealthy();
            var models = _service.GetLoadedModels();

            return Ok(new
            {
                status = isHealthy ? "Healthy" : "Unhealthy",
                isHealthy,
                loadedModels = models.Count,
                message = isHealthy
                    ? $"Service is operational with {models.Count} model(s) loaded."
                    : "No detection models are loaded. Place .onnx files in the Models folder and restart."
            });
        }
    }
}