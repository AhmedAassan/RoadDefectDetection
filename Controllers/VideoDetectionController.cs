using Microsoft.AspNetCore.Mvc;
using RoadDefectDetection.DTOs;
using RoadDefectDetection.Services;
using RoadDefectDetection.Services.Interfaces;

namespace RoadDefectDetection.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class VideoDetectionController : ControllerBase
    {
        private readonly IVideoDetectionService _videoService;
        private readonly IDetectionService _detectionService;
        private readonly AnnotatedVideoCache _videoCache;
        private readonly ILogger<VideoDetectionController> _logger;

        private const long MaxVideoSizeBytes = 500L * 1024 * 1024;

        public VideoDetectionController(
            IVideoDetectionService videoService,
            IDetectionService detectionService,
            AnnotatedVideoCache videoCache,
            ILogger<VideoDetectionController> logger)
        {
            _videoService = videoService;
            _detectionService = detectionService;
            _videoCache = videoCache;
            _logger = logger;
        }

        /// <summary>
        /// Analyzes a video for road defects and generates an annotated output video.
        /// </summary>
        [HttpPost("detect")]
        [RequestSizeLimit(524_288_000)]
        [RequestFormLimits(MultipartBodyLengthLimit = 524_288_000)]
        [ProducesResponseType(typeof(VideoDetectionResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
        public async Task<IActionResult> DetectVideo(
            IFormFile video,
            [FromForm] int frameInterval = 30,
            [FromForm] float? confidence = null,
            [FromForm] int maxFrames = 120)
        {
            if (!_detectionService.IsHealthy())
                return StatusCode(503, new { success = false, message = "No detection models loaded." });

            if (!await _videoService.IsAvailableAsync())
                return StatusCode(503, new { success = false, message = "FFmpeg is not available." });

            if (video == null || video.Length == 0)
                return BadRequest(new { success = false, message = "No video file provided." });

            string ext = Path.GetExtension(video.FileName).ToLowerInvariant();
            if (!_videoService.SupportedExtensions.Contains(ext))
                return BadRequest(new
                {
                    success = false,
                    message = $"Unsupported format '{ext}'. Supported: {string.Join(", ", _videoService.SupportedExtensions)}"
                });

            if (video.Length > MaxVideoSizeBytes)
                return BadRequest(new
                {
                    success = false,
                    message = $"Video ({video.Length / (1024.0 * 1024.0):F1}MB) exceeds 500MB limit."
                });

            if (frameInterval < 1 || frameInterval > 1000)
                return BadRequest(new { success = false, message = "frameInterval must be 1-1000." });

            if (maxFrames < 1 || maxFrames > 1000)
                return BadRequest(new { success = false, message = "maxFrames must be 1-1000." });

            if (confidence.HasValue && (confidence.Value < 0 || confidence.Value > 100))
                return BadRequest(new { success = false, message = "Confidence must be 0-100." });

            _logger.LogInformation("Video request: '{File}' ({Size:F1}MB)", video.FileName,
                video.Length / (1024.0 * 1024.0));

            byte[] videoBytes;
            using (var ms = new MemoryStream())
            {
                await video.CopyToAsync(ms);
                videoBytes = ms.ToArray();
            }

            var request = new VideoDetectionRequest
            {
                FrameInterval = frameInterval,
                Confidence = confidence,
                MaxFrames = maxFrames
            };

            var response = await _videoService.DetectVideoAsync(videoBytes, video.FileName, request);
            return Ok(response);
        }

        /// <summary>
        /// Streams the annotated video with bounding boxes drawn on each frame.
        /// </summary>
        [HttpGet("annotated/{id}")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public IActionResult GetAnnotatedVideo(string id)
        {
            var cached = _videoCache.Get(id);
            if (cached == null)
            {
                return NotFound(new
                {
                    success = false,
                    message = "Annotated video not found or expired. Videos are cached for 30 minutes."
                });
            }

            return File(cached.Data, cached.ContentType,
                $"annotated_{cached.OriginalName}",
                enableRangeProcessing: true);
        }

        [HttpGet("info")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public async Task<IActionResult> GetInfo()
        {
            bool ffmpegAvailable = await _videoService.IsAvailableAsync();
            return Ok(new
            {
                ffmpegAvailable,
                supportedFormats = _videoService.SupportedExtensions,
                maxFileSizeMB = MaxVideoSizeBytes / (1024 * 1024),
                features = new
                {
                    annotatedVideo = true,
                    objectTracking = true,
                    deduplication = true
                },
                defaults = new { frameInterval = 30, maxFrames = 120, confidence = "model default (15%)" },
                message = ffmpegAvailable ? "Video detection service is ready." : "FFmpeg not installed."
            });
        }

        [HttpGet("health")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public async Task<IActionResult> Health()
        {
            bool ffmpegOk = await _videoService.IsAvailableAsync();
            bool modelsOk = _detectionService.IsHealthy();

            return Ok(new
            {
                status = ffmpegOk && modelsOk ? "Healthy" : "Degraded",
                ffmpegAvailable = ffmpegOk,
                modelsLoaded = modelsOk,
                message = !ffmpegOk ? "FFmpeg not available."
                    : !modelsOk ? "No detection models loaded."
                    : "Fully operational."
            });
        }
    }
}