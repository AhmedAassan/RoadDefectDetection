using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using RoadDefectDetection.Configuration;
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
        private readonly VideoProcessingSettings _videoSettings;

        public VideoDetectionController(
            IVideoDetectionService videoService,
            IDetectionService detectionService,
            AnnotatedVideoCache videoCache,
            ILogger<VideoDetectionController> logger,
            IOptions<VideoProcessingSettings> videoOptions)
        {
            _videoService = videoService ?? throw new ArgumentNullException(nameof(videoService));
            _detectionService = detectionService ?? throw new ArgumentNullException(nameof(detectionService));
            _videoCache = videoCache ?? throw new ArgumentNullException(nameof(videoCache));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _videoSettings = videoOptions?.Value ?? throw new ArgumentNullException(nameof(videoOptions));
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
            [FromForm] int maxFrames = 120,
            CancellationToken cancellationToken = default)
        {
            if (!_detectionService.IsHealthy())
                return StatusCode(503, new
                {
                    success = false,
                    message = "No detection models loaded."
                });

            if (!_videoService.IsAvailable())
                return StatusCode(503, new
                {
                    success = false,
                    message = "OpenCV video processing is unavailable. " +
                              "Ensure the OpenCvSharp4 runtime package is installed."
                });

            if (video == null || video.Length == 0)
                return BadRequest(new { success = false, message = "No video file provided." });

            string ext = Path.GetExtension(video.FileName).ToLowerInvariant();
            if (!_videoService.SupportedExtensions.Contains(ext))
                return BadRequest(new
                {
                    success = false,
                    message = $"Unsupported format '{ext}'. " +
                              $"Supported: {string.Join(", ", _videoService.SupportedExtensions)}"
                });

            long maxBytes = (long)_videoSettings.MaxVideoSizeMB * 1024 * 1024;
            if (video.Length > maxBytes)
                return BadRequest(new
                {
                    success = false,
                    message = $"Video ({video.Length / (1024.0 * 1024.0):F1}MB) " +
                              $"exceeds {_videoSettings.MaxVideoSizeMB}MB limit."
                });

            if (frameInterval < 1 || frameInterval > 1000)
                return BadRequest(new { success = false, message = "frameInterval must be 1–1000." });

            if (maxFrames < 1 || maxFrames > 1000)
                return BadRequest(new { success = false, message = "maxFrames must be 1–1000." });

            if (confidence.HasValue && (confidence.Value < 0 || confidence.Value > 100))
                return BadRequest(new { success = false, message = "Confidence must be 0–100." });

            _logger.LogInformation(
                "Video request: '{File}' ({Size:F1}MB), interval={Interval}, maxFrames={Max}",
                video.FileName, video.Length / (1024.0 * 1024.0), frameInterval, maxFrames);

            byte[] videoBytes;
            using (var ms = new MemoryStream())
            {
                await video.CopyToAsync(ms, cancellationToken);
                videoBytes = ms.ToArray();
            }

            var request = new VideoDetectionRequest
            {
                FrameInterval = frameInterval,
                Confidence = confidence,
                MaxFrames = maxFrames
            };

            var response = await _videoService.DetectVideoAsync(
                videoBytes, video.FileName, request,
                cancellationToken: cancellationToken);

            return Ok(response);
        }

        /// <summary>
        /// Streams the annotated video from the in-memory cache.
        /// </summary>
        [HttpGet("annotated/{id}")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public IActionResult GetAnnotatedVideo(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
                return BadRequest(new { success = false, message = "Invalid video ID." });

            var cached = _videoCache.Get(id);
            if (cached == null)
            {
                return NotFound(new
                {
                    success = false,
                    message = "Annotated video not found or expired. " +
                              $"Videos are cached for {_videoSettings.CacheExpiryMinutes} minutes."
                });
            }

            return File(
                cached.Data,
                cached.ContentType,
                $"annotated_{cached.OriginalName}",
                enableRangeProcessing: true);
        }

        [HttpGet("info")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public IActionResult GetInfo()
        {
            bool available = _videoService.IsAvailable();
            return Ok(new
            {
                opencvAvailable = available,
                supportedFormats = _videoService.SupportedExtensions,
                maxFileSizeMB = _videoSettings.MaxVideoSizeMB,
                cacheExpiryMin = _videoSettings.CacheExpiryMinutes,
                maxCacheEntries = _videoSettings.MaxCacheEntries,
                features = new
                {
                    annotatedVideo = true,
                    objectTracking = true,
                    deduplication = true,
                    opencvPipeline = true
                },
                defaults = new
                {
                    frameInterval = _videoSettings.DefaultFrameInterval,
                    maxFrames = _videoSettings.DefaultMaxFrames,
                    confidence = "model default (15%)"
                },
                message = available
                    ? "Video detection service is ready (OpenCV pipeline)."
                    : "OpenCV native library unavailable. " +
                      "Install the correct OpenCvSharp4 runtime package."
            });
        }

        [HttpGet("health")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public IActionResult Health()
        {
            bool opencvOk = _videoService.IsAvailable();
            bool modelsOk = _detectionService.IsHealthy();
            bool allOk = opencvOk && modelsOk;

            return Ok(new
            {
                status = allOk ? "Healthy" : "Degraded",
                opencvAvailable = opencvOk,
                modelsLoaded = modelsOk,
                message = !opencvOk
                    ? "OpenCV native library unavailable."
                    : !modelsOk
                        ? "No detection models loaded."
                        : "Fully operational (OpenCV video pipeline)."
            });
        }
    }
}