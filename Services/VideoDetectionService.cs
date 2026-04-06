using Microsoft.Extensions.Options;
using OpenCvSharp;
using RoadDefectDetection.Configuration;
using RoadDefectDetection.DTOs;
using RoadDefectDetection.Services.Interfaces;
using System;
using System.Diagnostics;

namespace RoadDefectDetection.Services
{
    /// <summary>
    /// Video-based road defect detection service.
    /// 
    /// Uses OpenCvSharp4 for all video operations:
    ///   - Metadata probing   → VideoCapture properties
    ///   - Frame extraction   → VideoCapture.Read() in-memory loop
    ///   - Annotation drawing → OpenCvVideoAnnotator (Cv2.Rectangle / Cv2.PutText)
    ///   - Output encoding    → VideoWriter (MP4V / XVID)
    /// 
    /// No external processes (FFmpeg) are spawned at any point.
    /// </summary>
    public sealed class VideoDetectionService : IVideoDetectionService
    {
        private readonly IDetectionService _detectionService;
        private readonly ILogger<VideoDetectionService> _logger;
        private readonly VideoProcessingSettings _videoSettings;
        private readonly AnnotatedVideoCache _videoCache;

        public string[] SupportedExtensions => new[]
        {
            ".mp4", ".avi", ".mov", ".mkv",
            ".wmv", ".flv", ".webm"
        };

        public VideoDetectionService(
            IDetectionService detectionService,
            IOptions<VideoProcessingSettings> videoOptions,
            ILogger<VideoDetectionService> logger,
            AnnotatedVideoCache videoCache)
        {
            _detectionService = detectionService
                ?? throw new ArgumentNullException(nameof(detectionService));
            _logger = logger
                ?? throw new ArgumentNullException(nameof(logger));
            _videoCache = videoCache
                ?? throw new ArgumentNullException(nameof(videoCache));
            _videoSettings = videoOptions?.Value
                ?? throw new ArgumentNullException(nameof(videoOptions));
        }

        /// <inheritdoc />
        public bool IsAvailable()
        {
            // Validate that the OpenCV native library is present and functional
            // by opening and immediately releasing a VideoCapture.
            try
            {
                using var cap = new VideoCapture();
                // If the native library loaded successfully, IsOpened returns false
                // for an empty capture — but no exception means OpenCV works.
                _ = cap.IsOpened();
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "OpenCV native library is not available.");
                return false;
            }
        }

        /// <inheritdoc />
        public async Task<VideoDetectionResponse> DetectVideoAsync(
            byte[] videoBytes,
            string videoName,
            VideoDetectionRequest request,
            Action<int, int>? progress = null,
            CancellationToken cancellationToken = default)
        {
            var sw = Stopwatch.StartNew();

            // Write video bytes to a single temp file (OpenCV needs a path)
            string tempDir = Path.Combine(
                Path.GetTempPath(),
                "RoadDefect_" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(tempDir);

            string videoExt = Path.GetExtension(videoName).ToLowerInvariant();
            string videoPath = Path.Combine(tempDir, "input" + videoExt);
            string outputPath = Path.Combine(tempDir, "annotated.mp4");

            try
            {
                _logger.LogInformation(
                    "Video: '{N}' ({S:F1}MB), interval={I}, maxFrames={M}",
                    videoName, videoBytes.Length / (1024.0 * 1024.0),
                    request.FrameInterval, request.MaxFrames);

                await File.WriteAllBytesAsync(videoPath, videoBytes, cancellationToken);

                // ── 1. Probe metadata ────────────────────────────
                var meta = ProbeVideo(videoPath);
                if (meta == null)
                    return ErrorResponse("Failed to read video metadata with OpenCV.", videoName, sw);

                double fps = meta.Value.Fps;
                double dur = meta.Value.DurationSeconds;
                int width = meta.Value.Width;
                int height = meta.Value.Height;
                long totalFrm = meta.Value.TotalFrames;

                _logger.LogInformation(
                    "Video metadata: {W}x{H}, {Fps:F2}fps, {Dur:F1}s, {Total} frames",
                    width, height, fps, dur, totalFrm);

                // ── 2. Validate request parameters ───────────────
                int interval = Math.Max(1, request.FrameInterval);
                int maxFrames = Math.Max(1, request.MaxFrames);

                // ── 3. Extract & detect frames in-memory ─────────
                var tracker = new SimpleTracker(
                    _videoSettings.TrackerIouThreshold,
                    _videoSettings.TrackerMaxFramesLost);
                var frameResults = new List<FrameDetectionResult>();
                var smoothedFrameResults = new List<FrameDetectionResult>();

                int totalRaw = 0;
                int processed = 0;

                using (var capture = new VideoCapture(videoPath))
                {
                    if (!capture.IsOpened())
                        return ErrorResponse(
                            "OpenCV could not open the video file. " +
                            "Check that the file is a valid video format.",
                            videoName, sw);

                    using var mat = new Mat();
                    long sourceFrameIdx = 0;
                    int analyzedIdx = 0;

                    while (!cancellationToken.IsCancellationRequested &&
                           capture.Read(mat) && !mat.Empty())
                    {
                        // Only analyze every Nth frame
                        if (sourceFrameIdx % interval != 0)
                        {
                            sourceFrameIdx++;
                            continue;
                        }

                        if (analyzedIdx >= maxFrames) break;

                        double timestampSec = fps > 0
                            ? sourceFrameIdx / fps
                            : analyzedIdx * (interval / 30.0);

                        try
                        {
                            // Encode frame as JPEG bytes for the image detection pipeline
                            byte[] frameBytes = EncodeFrameAsJpeg(mat);

                            var result = await _detectionService.DetectAsync(
                                frameBytes,
                                $"{videoName}_f{sourceFrameIdx}",
                                request.Confidence,
                                cancellationToken);

                            totalRaw += result.TotalProblemsFound;

                            int actualFrame = (int)sourceFrameIdx;
                            var matches = tracker.Update(
                                result.Detections, actualFrame, timestampSec);

                            var trackIds = matches.Select(m => m.TrackId).ToList();
                            var smoothedDets = matches.Select(m => m.Detection).ToList();

                            // Raw frame result (for JSON response)
                            frameResults.Add(new FrameDetectionResult
                            {
                                FrameNumber = actualFrame,
                                TimestampSeconds = Math.Round(timestampSec, 3),
                                Timestamp = FormatTimestamp(timestampSec),
                                DefectsInFrame = result.TotalProblemsFound,
                                Detections = result.Detections,
                                TrackIds = trackIds
                            });

                            // Smoothed frame result (for annotation)
                            smoothedFrameResults.Add(new FrameDetectionResult
                            {
                                FrameNumber = actualFrame,
                                TimestampSeconds = Math.Round(timestampSec, 3),
                                Timestamp = FormatTimestamp(timestampSec),
                                DefectsInFrame = smoothedDets.Count,
                                Detections = smoothedDets,
                                TrackIds = trackIds
                            });

                            processed++;
                            analyzedIdx++;
                            progress?.Invoke(processed, maxFrames);

                            if (processed % 10 == 0)
                                _logger.LogInformation(
                                    "Processed {C} frames (sourceFrame={SF})...",
                                    processed, sourceFrameIdx);
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex,
                                "Frame {F} failed, skipping.", sourceFrameIdx);
                        }

                        sourceFrameIdx++;
                    }
                } // VideoCapture released here

                if (processed == 0)
                    return ErrorResponse(
                        "No frames could be extracted from the video.", videoName, sw);

                // ── 4. Annotated video generation ────────────────
                string? videoId = null;
                string? videoUrl = null;

                try
                {
                    var annotator = new OpenCvVideoAnnotator(_logger);
                    bool ok = await Task.Run(() =>
                        annotator.CreateAnnotatedVideo(
                            videoPath, outputPath,
                            smoothedFrameResults,
                            cancellationToken),
                        cancellationToken);

                    if (ok && File.Exists(outputPath))
                    {
                        var outBytes = await File.ReadAllBytesAsync(outputPath, cancellationToken);
                        if (outBytes.Length > 0)
                        {
                            videoId = _videoCache.Store(outBytes, videoName);
                            videoUrl = $"/api/videodetection/annotated/{videoId}";
                            _logger.LogInformation(
                                "Annotated video cached: '{Id}' ({S:F1}MB)",
                                videoId, outBytes.Length / (1024.0 * 1024.0));
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    _logger.LogInformation("Annotation cancelled.");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Annotation failed — continuing without annotated video.");
                }

                // ── 5. Build response ────────────────────────────
                sw.Stop();
                var unique = tracker.GetUniqueDefectSummary();
                var rawSum = frameResults
                    .SelectMany(f => f.Detections)
                    .GroupBy(d => d.Problem)
                    .ToDictionary(g => g.Key, g => g.Count());
                var uniSum = unique
                    .GroupBy(d => d.DefectClass)
                    .ToDictionary(g => g.Key, g => g.Count());
                int withDef = frameResults.Count(f => f.DefectsInFrame > 0);
                double ms = sw.Elapsed.TotalMilliseconds;

                return new VideoDetectionResponse
                {
                    Success = true,
                    Message = unique.Count > 0
                        ? $"Found {unique.Count} unique defect(s) ({totalRaw} raw detections " +
                          $"across {withDef} of {processed} frames). " +
                          $"Tracking eliminated {Math.Max(0, totalRaw - unique.Count)} duplicate(s)."
                        : $"No defects detected across {processed} analyzed frames.",
                    VideoName = videoName,
                    VideoDurationSeconds = Math.Round(dur, 2),
                    VideoDuration = FormatTimestamp(dur),
                    VideoFps = Math.Round(fps, 2),
                    VideoWidth = width,
                    VideoHeight = height,
                    TotalFrameCount = totalFrm,
                    FramesAnalyzed = processed,
                    FrameIntervalUsed = interval,
                    ProcessingTimeMs = Math.Round(ms, 1),
                    AvgTimePerFrameMs = Math.Round(processed > 0 ? ms / processed : 0, 1),
                    TotalRawDetections = totalRaw,
                    TotalUniqueDefects = unique.Count,
                    FramesWithDefects = withDef,
                    DefectSummary = uniSum,
                    RawDefectSummary = rawSum,
                    UniqueDefects = unique,
                    FrameResults = frameResults,
                    AnnotatedVideoId = videoId,
                    AnnotatedVideoUrl = videoUrl
                };
            }
            catch (OperationCanceledException)
            {
                sw.Stop();
                _logger.LogInformation("Video detection cancelled for '{V}'.", videoName);
                return ErrorResponse("Video detection was cancelled.", videoName, sw);
            }
            catch (Exception ex)
            {
                sw.Stop();
                _logger.LogError(ex, "Error processing video '{V}'.", videoName);
                return ErrorResponse("Video processing failed. See server logs.", videoName, sw);
            }
            finally
            {
                Cleanup(tempDir);
            }
        }

        // ── Helpers ──────────────────────────────────────────────

        /// <summary>
        /// Probes video metadata using OpenCV VideoCapture properties.
        /// No external process required.
        /// </summary>
        private VideoMeta? ProbeVideo(string path)
        {
            try
            {
                using var cap = new VideoCapture(path);
                if (!cap.IsOpened())
                {
                    _logger.LogWarning("OpenCV could not open '{Path}' for probing.", path);
                    return null;
                }

                double fps = cap.Get(VideoCaptureProperties.Fps);
                int width = (int)cap.Get(VideoCaptureProperties.FrameWidth);
                int height = (int)cap.Get(VideoCaptureProperties.FrameHeight);
                long count = (long)cap.Get(VideoCaptureProperties.FrameCount);

                // Some codecs/containers don't report frame count reliably
                if (fps <= 0) fps = 25;

                double duration = count > 0 && fps > 0
                    ? count / fps
                    : EstimateDuration(cap, fps);

                if (width <= 0 || height <= 0)
                {
                    _logger.LogWarning(
                        "Invalid dimensions from OpenCV probe: {W}x{H}", width, height);
                    return null;
                }

                return new VideoMeta
                {
                    Fps = fps,
                    Width = width,
                    Height = height,
                    TotalFrames = count > 0 ? count : (long)(duration * fps),
                    DurationSeconds = duration
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ProbeVideo failed for '{Path}'.", path);
                return null;
            }
        }

        /// <summary>
        /// Estimates video duration by seeking to the end when frame count
        /// is not available from container metadata.
        /// </summary>
        private static double EstimateDuration(VideoCapture cap, double fps)
        {
            // Try position-in-milliseconds at the end
            try
            {
                cap.Set(VideoCaptureProperties.PosMsec, double.MaxValue);
                double endMs = cap.Get(VideoCaptureProperties.PosMsec);
                cap.Set(VideoCaptureProperties.PosMsec, 0); // rewind
                if (endMs > 0) return endMs / 1000.0;
            }
            catch { /* ignore */ }

            return 0;
        }

        /// <summary>
        /// Encodes an OpenCV Mat frame as JPEG bytes compatible with the
        /// image detection pipeline (SixLabors.ImageSharp).
        /// </summary>
        private static byte[] EncodeFrameAsJpeg(Mat frame)
        {
            Cv2.ImEncode(".jpg", frame, out var buffer,
                new ImageEncodingParam(ImwriteFlags.JpegQuality, 90));
            return buffer;
        }

        private static VideoDetectionResponse ErrorResponse(
            string msg, string name, Stopwatch sw)
        {
            return new VideoDetectionResponse
            {
                Success = false,
                Message = msg,
                VideoName = name,
                ProcessingTimeMs = Math.Round(sw.Elapsed.TotalMilliseconds, 1)
            };
        }

        private static string FormatTimestamp(double seconds)
        {
            var ts = TimeSpan.FromSeconds(seconds);
            return ts.TotalHours >= 1
                ? ts.ToString(@"hh\:mm\:ss\.f")
                : ts.ToString(@"mm\:ss\.f");
        }

        private void Cleanup(string dir)
        {
            try
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to clean up temp dir: {Dir}", dir);
            }
        }

        private struct VideoMeta
        {
            public int Width;
            public int Height;
            public double Fps;
            public double DurationSeconds;
            public long TotalFrames;
        }
    }
}