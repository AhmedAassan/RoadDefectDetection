using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using RoadDefectDetection.Configuration;
using RoadDefectDetection.DTOs;
using RoadDefectDetection.Services.Interfaces;

namespace RoadDefectDetection.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class DetectionController : ControllerBase
    {
        private readonly IDetectionService _service;
        private readonly IExternalMappingService _mappingService;
        private readonly ILogger<DetectionController> _logger;
        private readonly DetectionSettings _settings;

        private static readonly string[] AllowedExtensions =
            { ".jpg", ".jpeg", ".png", ".bmp" };

        public DetectionController(
            IDetectionService service,
            IExternalMappingService mappingService,
            ILogger<DetectionController> logger,
            IOptions<DetectionSettings> settings)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
            _mappingService = mappingService ?? throw new ArgumentNullException(nameof(mappingService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _settings = settings?.Value ?? throw new ArgumentNullException(nameof(settings));
        }

        // ── Standard detection ───────────────────────────────────

        [HttpPost("detect")]
        [ProducesResponseType(typeof(DetectionResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
        public async Task<IActionResult> Detect(
            IFormFile image,
            [FromForm] float? confidence = null,
            CancellationToken cancellationToken = default)
        {
            var validation = ValidateImage(image, confidence);
            if (validation != null) return validation;

            byte[] bytes;
            using (var ms = new MemoryStream())
            {
                await image.CopyToAsync(ms, cancellationToken);
                bytes = ms.ToArray();
            }

            var response = await _service.DetectAsync(
                bytes, image.FileName, confidence, cancellationToken);
            return Ok(response);
        }

        [HttpPost("detect-multiple")]
        [ProducesResponseType(typeof(List<DetectionResponse>), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
        public async Task<IActionResult> DetectMultiple(
            List<IFormFile> images,
            [FromForm] float? confidence = null,
            CancellationToken cancellationToken = default)
        {
            if (!_service.IsHealthy())
                return StatusCode(503, Problem503());

            var validationResult = ValidateImageList(images, confidence);
            if (validationResult != null) return validationResult;

            var data = await ReadFilesAsync(images, cancellationToken);
            var responses = await _service.DetectMultipleAsync(
                data, confidence, cancellationToken);
            return Ok(responses);
        }

        // ── Mapped detection ─────────────────────────────────────

        [HttpPost("detect-mapped")]
        [ProducesResponseType(typeof(MappedDetectionResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
        public async Task<IActionResult> DetectMapped(
            IFormFile image,
            [FromForm] float? confidence = null,
            CancellationToken cancellationToken = default)
        {
            var validation = ValidateImage(image, confidence);
            if (validation != null) return validation;

            byte[] bytes;
            using (var ms = new MemoryStream())
            {
                await image.CopyToAsync(ms, cancellationToken);
                bytes = ms.ToArray();
            }

            var response = await _service.DetectAsync(
                bytes, image.FileName, confidence, cancellationToken);
            return Ok(_mappingService.MapResponse(response));
        }

        [HttpPost("detect-mapped-multiple")]
        [ProducesResponseType(typeof(List<MappedDetectionResponse>), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
        public async Task<IActionResult> DetectMappedMultiple(
            List<IFormFile> images,
            [FromForm] float? confidence = null,
            CancellationToken cancellationToken = default)
        {
            if (!_service.IsHealthy())
                return StatusCode(503, Problem503());

            var validationResult = ValidateImageList(images, confidence);
            if (validationResult != null) return validationResult;

            var data = await ReadFilesAsync(images, cancellationToken);
            var responses = await _service.DetectMultipleAsync(
                data, confidence, cancellationToken);
            var mapped = responses.Select(_mappingService.MapResponse).ToList();
            return Ok(mapped);
        }

        // ── Info endpoints ───────────────────────────────────────

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

        [HttpGet("mappings/model/{modelId:int}")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public IActionResult GetMappingsForModel(int modelId)
        {
            var mappings = _mappingService.GetMappingsForModel(modelId);
            return Ok(new { modelId, totalMappings = mappings.Count, mappings });
        }

        [HttpGet("models")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public IActionResult GetModels()
        {
            var models = _service.GetLoadedModels();
            return Ok(new { totalModels = models.Count, models });
        }

        [HttpGet("health")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public IActionResult Health()
        {
            bool healthy = _service.IsHealthy();
            var models = _service.GetLoadedModels();
            var mappings = _mappingService.GetAllMappings();
            return Ok(new
            {
                status = healthy ? "Healthy" : "Unhealthy",
                isHealthy = healthy,
                loadedModels = models.Count,
                configuredMappings = mappings.Count,
                message = healthy
                    ? $"Service operational with {models.Count} model(s) and {mappings.Count} mapping(s)."
                    : "No detection models loaded. Place .onnx files in the Models folder and restart."
            });
        }

        // ── Private helpers ──────────────────────────────────────

        private IActionResult? ValidateImage(IFormFile? image, float? confidence)
        {
            if (!_service.IsHealthy())
                return StatusCode(503, Problem503());

            if (confidence.HasValue && (confidence.Value < 0 || confidence.Value > 100))
                return BadRequest(new { success = false, message = "Confidence must be between 0 and 100." });

            if (image == null || image.Length == 0)
                return BadRequest(new { success = false, message = "No image file provided." });

            string ext = Path.GetExtension(image.FileName).ToLowerInvariant();
            if (!AllowedExtensions.Contains(ext))
                return BadRequest(new
                {
                    success = false,
                    message = $"Invalid format '{ext}'. Allowed: {string.Join(", ", AllowedExtensions)}"
                });

            long maxBytes = (long)_settings.MaxImageSizeMB * 1024 * 1024;
            if (image.Length > maxBytes)
                return BadRequest(new
                {
                    success = false,
                    message = $"File size ({image.Length / (1024.0 * 1024.0):F1}MB) exceeds {_settings.MaxImageSizeMB}MB limit."
                });

            return null;
        }

        private IActionResult? ValidateImageList(List<IFormFile>? images, float? confidence)
        {
            if (confidence.HasValue && (confidence.Value < 0 || confidence.Value > 100))
                return BadRequest(new { success = false, message = "Confidence must be between 0 and 100." });

            if (images == null || images.Count == 0)
                return BadRequest(new { success = false, message = "No image files provided." });

            long maxBytes = (long)_settings.MaxImageSizeMB * 1024 * 1024;
            for (int i = 0; i < images.Count; i++)
            {
                var f = images[i];
                if (f == null || f.Length == 0)
                    return BadRequest(new { success = false, message = $"File at index {i} is empty." });

                string ext = Path.GetExtension(f.FileName).ToLowerInvariant();
                if (!AllowedExtensions.Contains(ext))
                    return BadRequest(new { success = false, message = $"File '{f.FileName}' has invalid format." });

                if (f.Length > maxBytes)
                    return BadRequest(new { success = false, message = $"File '{f.FileName}' exceeds size limit." });
            }
            return null;
        }

        private static async Task<List<(byte[] bytes, string name)>> ReadFilesAsync(
            List<IFormFile> images,
            CancellationToken cancellationToken)
        {
            var result = new List<(byte[], string)>(images.Count);
            foreach (var f in images)
            {
                using var ms = new MemoryStream();
                await f.CopyToAsync(ms, cancellationToken);
                result.Add((ms.ToArray(), f.FileName));
            }
            return result;
        }

        private static object Problem503() =>
            new { success = false, message = "No detection models are currently loaded." };
    }
}