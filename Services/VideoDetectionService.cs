using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using RoadDefectDetection.Configuration;
using RoadDefectDetection.DTOs;
using RoadDefectDetection.Services.Interfaces;

namespace RoadDefectDetection.Services
{
    public sealed class VideoDetectionService : IVideoDetectionService
    {
        private readonly IDetectionService _detectionService;
        private readonly ILogger<VideoDetectionService> _logger;
        private readonly DetectionSettings _settings;
        private readonly AnnotatedVideoCache _videoCache;
        private readonly string _ffmpegPath;
        private readonly string _ffprobePath;

        private const float TrackerIouThreshold = 0.25f;
        private const int TrackerMaxFramesLost = 5;

        public string[] SupportedExtensions => new[]
        {
            ".mp4", ".avi", ".mov", ".mkv",
            ".wmv", ".flv", ".webm"
        };

        public VideoDetectionService(
            IDetectionService detectionService,
            IConfiguration configuration,
            ILogger<VideoDetectionService> logger,
            AnnotatedVideoCache videoCache)
        {
            _detectionService = detectionService
                ?? throw new ArgumentNullException(nameof(detectionService));
            _logger = logger
                ?? throw new ArgumentNullException(nameof(logger));
            _videoCache = videoCache
                ?? throw new ArgumentNullException(nameof(videoCache));

            _settings = new DetectionSettings();
            configuration.GetSection("DetectionSettings").Bind(_settings);

            var configPath = configuration.GetValue<string>("FFmpeg:Path") ?? "";

            if (!string.IsNullOrEmpty(configPath))
            {
                _ffmpegPath = Path.Combine(configPath, "ffmpeg");
                _ffprobePath = Path.Combine(configPath, "ffprobe");

                if (OperatingSystem.IsWindows())
                {
                    if (!_ffmpegPath.EndsWith(".exe")) _ffmpegPath += ".exe";
                    if (!_ffprobePath.EndsWith(".exe")) _ffprobePath += ".exe";
                }
            }
            else
            {
                _ffmpegPath = "ffmpeg";
                _ffprobePath = "ffprobe";
            }
        }

        public async Task<bool> IsAvailableAsync()
        {
            try
            {
                return await TestExeAsync(_ffmpegPath, "-version")
                    && await TestExeAsync(_ffprobePath, "-version");
            }
            catch { return false; }
        }

        public async Task<VideoDetectionResponse> DetectVideoAsync(
            byte[] videoBytes,
            string videoName,
            VideoDetectionRequest request,
            Action<int, int>? progress = null)
        {
            var sw = Stopwatch.StartNew();

            string tempDir = Path.Combine(Path.GetTempPath(),
                "RoadDefect_" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(tempDir);

            string videoPath = Path.Combine(tempDir,
                "input" + Path.GetExtension(videoName).ToLowerInvariant());
            string framesDir = Path.Combine(tempDir, "frames");
            string outputPath = Path.Combine(tempDir, "annotated.mp4");
            Directory.CreateDirectory(framesDir);

            try
            {
                _logger.LogInformation(
                    "Video: '{N}' ({S:F1}MB) interval={I} max={M}",
                    videoName, videoBytes.Length / (1024.0 * 1024.0),
                    request.FrameInterval, request.MaxFrames);

                await File.WriteAllBytesAsync(videoPath, videoBytes);

                // ── Probe ────────────────────────────────────────
                var meta = await ProbeAsync(videoPath);
                if (meta == null)
                    return ErrorResponse("Failed to read video metadata.",
                        videoName, sw);

                double fps = meta.Value.Fps;
                double dur = meta.Value.Dur;
                int w = meta.Value.W, h = meta.Value.H;
                long totalFrames = (long)(fps * dur);

                // ── Extraction rate ──────────────────────────────
                int interval = Math.Max(1, request.FrameInterval);
                int maxFrames = Math.Max(1, request.MaxFrames);
                double extractFps = fps / interval;
                if (extractFps * dur > maxFrames)
                    extractFps = maxFrames / dur;
                extractFps = Math.Max(0.1, extractFps);

                // ── Extract ──────────────────────────────────────
                await ExtractAsync(videoPath, framesDir, extractFps);

                var files = Directory.GetFiles(framesDir, "*.jpg")
                    .OrderBy(f => f).Take(maxFrames).ToList();

                if (files.Count == 0)
                    return ErrorResponse("No frames extracted.", videoName, sw);

                _logger.LogInformation("Extracted {C} frames. Detecting...",
                    files.Count);

                // ── Detect + Track ───────────────────────────────
                var tracker = new SimpleTracker(TrackerIouThreshold, TrackerMaxFramesLost);
                var frameResults = new List<FrameDetectionResult>();
                // For video annotation, we store SMOOTHED detections
                var smoothedFrameResults = new List<FrameDetectionResult>();
                int processed = 0, totalRaw = 0;

                foreach (var file in files)
                {
                    try
                    {
                        int idx = ParseFrameIndex(file);
                        int actualFrame = (int)(idx * interval);
                        double ts = extractFps > 0 ? idx / extractFps : 0;

                        byte[] frameBytes = await File.ReadAllBytesAsync(file);

                        var result = await _detectionService.DetectAsync(
                            frameBytes,
                            $"{videoName}_f{idx}",
                            request.Confidence);

                        totalRaw += result.TotalProblemsFound;

                        // Track with smoothing
                        var matches = tracker.Update(
                            result.Detections, actualFrame, ts);

                        var trackIds = matches.Select(m => m.TrackId).ToList();

                        // Raw frame result (for JSON response)
                        frameResults.Add(new FrameDetectionResult
                        {
                            FrameNumber = actualFrame,
                            TimestampSeconds = Math.Round(ts, 3),
                            Timestamp = FormatTs(ts),
                            DefectsInFrame = result.TotalProblemsFound,
                            Detections = result.Detections,
                            TrackIds = trackIds
                        });

                        // SMOOTHED frame result (for video annotation)
                        var smoothedDets = matches.Select(m => m.Detection).ToList();
                        smoothedFrameResults.Add(new FrameDetectionResult
                        {
                            FrameNumber = actualFrame,
                            TimestampSeconds = Math.Round(ts, 3),
                            Timestamp = FormatTs(ts),
                            DefectsInFrame = smoothedDets.Count,
                            Detections = smoothedDets,
                            TrackIds = trackIds
                        });

                        processed++;
                        progress?.Invoke(processed, files.Count);

                        if (processed % 10 == 0)
                            _logger.LogInformation("Processed {C}/{T}...",
                                processed, files.Count);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Frame failed: {F}", file);
                    }
                }

                // ── Annotated video (uses SMOOTHED boxes) ────────
                string? videoId = null, videoUrl = null;
                try
                {
                    var annotator = new VideoAnnotator(_ffmpegPath, _logger);

                    // Pass smoothedFrameResults to annotator
                    bool ok = await annotator.CreateAnnotatedVideoAsync(
                        videoPath, outputPath,
                        smoothedFrameResults,
                        fps, interval);

                    if (ok && File.Exists(outputPath))
                    {
                        var outBytes = await File.ReadAllBytesAsync(outputPath);
                        if (outBytes.Length > 0)
                        {
                            videoId = _videoCache.Store(outBytes, videoName);
                            videoUrl = $"/api/videodetection/annotated/{videoId}";
                            _logger.LogInformation("Cached: {Id} ({S:F1}MB)",
                                videoId, outBytes.Length / (1024.0 * 1024.0));
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Annotation failed.");
                }

                sw.Stop();

                // ── Response ─────────────────────────────────────
                var unique = tracker.GetUniqueDefectSummary();
                var rawSum = frameResults.SelectMany(f => f.Detections)
                    .GroupBy(d => d.Problem).ToDictionary(g => g.Key, g => g.Count());
                var uniSum = unique.GroupBy(d => d.DefectClass)
                    .ToDictionary(g => g.Key, g => g.Count());

                int withDef = frameResults.Count(f => f.DefectsInFrame > 0);
                double ms = sw.Elapsed.TotalMilliseconds;

                return new VideoDetectionResponse
                {
                    Success = true,
                    Message = unique.Count > 0
                        ? $"Found {unique.Count} unique defect(s) ({totalRaw} raw " +
                          $"across {withDef} of {processed} frames). " +
                          $"Tracking eliminated {totalRaw - unique.Count} duplicate(s)."
                        : $"No defects in {processed} frames.",
                    VideoName = videoName,
                    VideoDurationSeconds = Math.Round(dur, 2),
                    VideoDuration = FormatTs(dur),
                    VideoFps = Math.Round(fps, 2),
                    VideoWidth = w,
                    VideoHeight = h,
                    TotalFrameCount = totalFrames,
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
                    FrameResults = frameResults, // Raw for JSON
                    AnnotatedVideoId = videoId,
                    AnnotatedVideoUrl = videoUrl
                };
            }
            catch (Exception ex)
            {
                sw.Stop();
                _logger.LogError(ex, "Error: '{V}'", videoName);
                return ErrorResponse($"Failed: {ex.Message}", videoName, sw);
            }
            finally
            {
                Cleanup(tempDir);
            }
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

        // ═════════════════════════════════════════════════════════
        // PROBE / EXTRACT / HELPERS
        // ═════════════════════════════════════════════════════════

        private async Task<VMeta?> ProbeAsync(string path)
        {
            try
            {
                string args = $"-v error -select_streams v:0 " +
                    $"-show_entries stream=width,height,r_frame_rate,duration " +
                    $"-show_entries format=duration -of csv=p=0:s=, \"{path}\"";
                string output = await RunAsync(_ffprobePath, args);

                var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(l => l.Trim()).Where(l => l.Length > 0).ToList();

                int pw = 0, ph = 0; double pf = 30, pd = 0;
                foreach (var line in lines)
                {
                    var p = line.Split(',');
                    if (p.Length >= 3)
                    {
                        if (int.TryParse(p[0].Trim(), out int vw) && vw > 0) pw = vw;
                        if (p.Length > 1 && int.TryParse(p[1].Trim(), out int vh) && vh > 0) ph = vh;
                        if (p.Length > 2) pf = ParseFps(p[2].Trim());
                        if (p.Length > 3 && double.TryParse(p[3].Trim(),
                            NumberStyles.Float, CultureInfo.InvariantCulture, out double sd)) pd = sd;
                    }
                    else if (p.Length == 1 && double.TryParse(p[0].Trim(),
                        NumberStyles.Float, CultureInfo.InvariantCulture, out double fd) && fd > 0)
                    { if (pd <= 0) pd = fd; }
                }

                if (pd <= 0)
                {
                    string dOut = await RunAsync(_ffprobePath,
                        $"-v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 \"{path}\"");
                    double.TryParse(dOut.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out pd);
                }

                if (pw <= 0 || ph <= 0 || pd <= 0)
                    return await SimpleProbeAsync(path);

                return new VMeta { W = pw, H = ph, Fps = pf, Dur = pd };
            }
            catch { return await SimpleProbeAsync(path); }
        }

        private async Task<VMeta?> SimpleProbeAsync(string path)
        {
            try
            {
                string dO = await RunAsync(_ffprobePath,
                    $"-v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 \"{path}\"");
                double.TryParse(dO.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double dur);

                string rO = await RunAsync(_ffprobePath,
                    $"-v error -select_streams v:0 -show_entries stream=width,height -of csv=s=x:p=0 \"{path}\"");
                var rp = rO.Trim().Split('x');
                int w = 0, h = 0;
                if (rp.Length >= 2) { int.TryParse(rp[0], out w); int.TryParse(rp[1], out h); }

                string fO = await RunAsync(_ffprobePath,
                    $"-v error -select_streams v:0 -show_entries stream=r_frame_rate -of default=noprint_wrappers=1:nokey=1 \"{path}\"");
                double fp = ParseFps(fO.Trim());

                if (dur <= 0 || w <= 0 || h <= 0) return null;
                return new VMeta { W = w, H = h, Fps = fp > 0 ? fp : 30, Dur = dur };
            }
            catch { return null; }
        }

        private async Task ExtractAsync(string video, string outDir, double fps)
        {
            string pat = Path.Combine(outDir, "frame_%05d.jpg");
            string fs = fps.ToString("F4", CultureInfo.InvariantCulture);
            await RunAsync(_ffmpegPath,
                $"-i \"{video}\" -vf \"fps={fs}\" -q:v 2 -vsync vfr \"{pat}\"", 300);
        }

        private async Task<string> RunAsync(string exe, string args, int timeout = 60)
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = new Process { StartInfo = psi };
            p.Start();
            var so = p.StandardOutput.ReadToEndAsync();
            var se = p.StandardError.ReadToEndAsync();
            bool ok = await Task.Run(() => p.WaitForExit(timeout * 1000));
            if (!ok) { try { p.Kill(true); } catch { } throw new TimeoutException(); }
            string stdout = await so, stderr = await se;
            return !string.IsNullOrEmpty(stdout) ? stdout : stderr;
        }

        private async Task<bool> TestExeAsync(string exe, string args)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = args,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var p = Process.Start(psi);
                if (p == null) return false;
                bool ok = await Task.Run(() => p.WaitForExit(5000));
                if (!ok) { try { p.Kill(); } catch { } return false; }
                return p.ExitCode == 0;
            }
            catch { return false; }
        }

        private static double ParseFps(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return 30;
            s = s.Trim();
            if (s.Contains('/'))
            {
                var p = s.Split('/');
                if (p.Length == 2 &&
                    double.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double n) &&
                    double.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double d) && d > 0)
                    return n / d;
            }
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double f) && f > 0 ? f : 30;
        }

        private static int ParseFrameIndex(string path)
        {
            var m = Regex.Match(Path.GetFileNameWithoutExtension(path), @"(\d+)");
            return m.Success && int.TryParse(m.Groups[1].Value, out int i) ? Math.Max(0, i - 1) : 0;
        }

        private static string FormatTs(double s)
        {
            var ts = TimeSpan.FromSeconds(s);
            return ts.TotalHours >= 1 ? ts.ToString(@"hh\:mm\:ss\.f") : ts.ToString(@"mm\:ss\.f");
        }

        private void Cleanup(string dir)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
            catch (Exception ex) { _logger.LogWarning(ex, "Cleanup: {D}", dir); }
        }

        private struct VMeta { public int W, H; public double Fps, Dur; }
    }
}