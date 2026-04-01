using System.Diagnostics;
using System.Globalization;
using System.Text;
using RoadDefectDetection.DTOs;

namespace RoadDefectDetection.Services
{
    /// <summary>
    /// Creates annotated videos using FFmpeg. Uses filter_script files
    /// to avoid shell escaping issues. Includes proper font handling
    /// and text label rendering.
    /// 
    /// Text rendering strategy:
    /// 1. Try drawtext with fontfile= (explicit font path)
    /// 2. Fallback: drawtext with font=Arial (system font)
    /// 3. Fallback: drawbox only (no text, but still shows boxes)
    /// 
    /// Box coordinates use SMOOTHED positions from the tracker,
    /// eliminating jitter between frames.
    /// </summary>
    public class VideoAnnotator
    {
        private readonly string _ffmpegPath;
        private readonly ILogger _logger;

        private static readonly string[] BoxColors = new[]
        {
            "red", "green", "cyan", "yellow", "magenta",
            "white", "orange", "lime", "blue", "pink"
        };

        // Known font paths by OS
        private static readonly string[] WindowsFontPaths = new[]
        {
            @"C\:/Windows/Fonts/arial.ttf",
            @"C\:/Windows/Fonts/consola.ttf",
            @"C\:/Windows/Fonts/segoeui.ttf",
            @"C\:/Windows/Fonts/tahoma.ttf",
            @"C\:/Windows/Fonts/verdana.ttf",
            @"C\:/Windows/Fonts/calibri.ttf"
        };

        private static readonly string[] LinuxFontPaths = new[]
        {
            "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
            "/usr/share/fonts/truetype/liberation/LiberationSans-Regular.ttf",
            "/usr/share/fonts/TTF/DejaVuSans.ttf",
            "/usr/share/fonts/dejavu/DejaVuSans.ttf"
        };

        private string? _resolvedFontPath;
        private bool _fontResolved;

        public VideoAnnotator(string ffmpegPath, ILogger logger)
        {
            _ffmpegPath = ffmpegPath;
            _logger = logger;
        }

        /// <summary>
        /// Creates annotated video. Uses smoothed box coordinates from tracker.
        /// Three fallback levels ensure something always works.
        /// </summary>
        public async Task<bool> CreateAnnotatedVideoAsync(
            string inputVideoPath,
            string outputVideoPath,
            List<FrameDetectionResult> frameResults,
            double fps,
            int frameInterval)
        {
            bool hasDetections = frameResults.Any(f => f.DefectsInFrame > 0);
            if (!hasDetections)
            {
                _logger.LogInformation("No detections. Copying original.");
                return await CopyVideoAsync(inputVideoPath, outputVideoPath);
            }

            // Resolve font path once
            if (!_fontResolved)
            {
                _resolvedFontPath = ResolveFontPath();
                _fontResolved = true;
                if (_resolvedFontPath != null)
                    _logger.LogInformation("Font resolved: {Font}", _resolvedFontPath);
                else
                    _logger.LogWarning("No font found. Text labels may not render.");
            }

            // Try Level 1: Full annotation (boxes + text with fontfile)
            bool success = await TryAnnotateAsync(
                inputVideoPath, outputVideoPath, frameResults,
                fps, frameInterval, includeText: true, useFontFile: true);

            if (success) return true;

            // Try Level 2: Full annotation (boxes + text without fontfile)
            _logger.LogWarning("Level 1 failed. Trying Level 2 (system font)...");
            success = await TryAnnotateAsync(
                inputVideoPath, outputVideoPath, frameResults,
                fps, frameInterval, includeText: true, useFontFile: false);

            if (success) return true;

            // Try Level 3: Boxes only (no text)
            _logger.LogWarning("Level 2 failed. Trying Level 3 (boxes only)...");
            success = await TryAnnotateAsync(
                inputVideoPath, outputVideoPath, frameResults,
                fps, frameInterval, includeText: false, useFontFile: false);

            if (success) return true;

            // Level 4: Copy original
            _logger.LogWarning("All annotation attempts failed. Copying original.");
            return await CopyVideoAsync(inputVideoPath, outputVideoPath);
        }

        private async Task<bool> TryAnnotateAsync(
            string inputPath,
            string outputPath,
            List<FrameDetectionResult> frameResults,
            double fps,
            int frameInterval,
            bool includeText,
            bool useFontFile)
        {
            try
            {
                string filterContent = BuildFilterScript(
                    frameResults, fps, frameInterval,
                    includeText, useFontFile);

                if (string.IsNullOrEmpty(filterContent))
                    return await CopyVideoAsync(inputPath, outputPath);

                string filterPath = Path.Combine(
                    Path.GetDirectoryName(outputPath)!,
                    $"filter_{(includeText ? "full" : "boxes")}.txt");

                await File.WriteAllTextAsync(filterPath, filterContent,
                    new UTF8Encoding(false));

                _logger.LogDebug("Filter ({Mode}): {Lines} lines, {Size} bytes",
                    includeText ? (useFontFile ? "fontfile" : "sysfont") : "boxes-only",
                    filterContent.Split('\n').Length, filterContent.Length);

                string args =
                    $"-y -i \"{inputPath}\" " +
                    $"-filter_script:v \"{filterPath}\" " +
                    $"-c:v libx264 -preset fast -crf 23 " +
                    $"-pix_fmt yuv420p -c:a copy " +
                    $"-movflags +faststart \"{outputPath}\"";

                var (exitCode, _, stderr) = await RunFFmpegAsync(args, 600);

                if (File.Exists(outputPath) && new FileInfo(outputPath).Length > 0)
                {
                    _logger.LogInformation(
                        "Annotated video created ({Mode}): {Size:F1}MB",
                        includeText ? "with text" : "boxes only",
                        new FileInfo(outputPath).Length / (1024.0 * 1024.0));
                    return true;
                }

                if (!string.IsNullOrEmpty(stderr))
                {
                    string tail = stderr.Length > 600 ? stderr[^600..] : stderr;
                    _logger.LogWarning("FFmpeg stderr: {Err}", tail);
                }

                // Clean up failed output
                if (File.Exists(outputPath)) File.Delete(outputPath);
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Annotation attempt failed.");
                if (File.Exists(outputPath)) File.Delete(outputPath);
                return false;
            }
        }

        /// <summary>
        /// Builds the FFmpeg filter script content.
        /// Uses filter_script format (no shell escaping needed).
        /// 
        /// Escaping rules for filter_script files:
        /// - Commas in function args: use \, 
        /// - Colons in text: use \:
        /// - Single quotes: don't nest, use for enable expressions
        /// - Backslash in Windows paths: use forward slash
        /// </summary>
        private string BuildFilterScript(
            List<FrameDetectionResult> frameResults,
            double fps,
            int frameInterval,
            bool includeText,
            bool useFontFile)
        {
            var filters = new List<string>();
            double frameDuration = frameInterval / fps;

            string? fontClause = null;
            if (includeText)
            {
                if (useFontFile && _resolvedFontPath != null)
                {
                    fontClause = $"fontfile='{_resolvedFontPath}'";
                }
                // If no fontfile, FFmpeg will try to use a default font
            }

            for (int fi = 0; fi < frameResults.Count; fi++)
            {
                var frame = frameResults[fi];
                if (frame.DefectsInFrame == 0) continue;

                double startTime = frame.TimestampSeconds;
                double endTime = (fi + 1 < frameResults.Count)
                    ? frameResults[fi + 1].TimestampSeconds
                    : startTime + frameDuration;

                // Ensure minimum display duration
                if (endTime - startTime < 0.3)
                    endTime = startTime + frameDuration;

                string st = startTime.ToString("F3", CultureInfo.InvariantCulture);
                string et = endTime.ToString("F3", CultureInfo.InvariantCulture);
                string enableExpr = $"enable='between(t\\,{st}\\,{et})'";

                for (int di = 0; di < frame.Detections.Count; di++)
                {
                    var det = frame.Detections[di];
                    var box = det.Box;

                    int trackId = (frame.TrackIds != null && di < frame.TrackIds.Count)
                        ? frame.TrackIds[di] : di + 1;

                    string color = BoxColors[(trackId - 1) % BoxColors.Length];

                    // Clamp coordinates
                    int bx = Math.Max(0, (int)Math.Round(box.X));
                    int by = Math.Max(0, (int)Math.Round(box.Y));
                    int bw = Math.Max(10, (int)Math.Round(box.Width));
                    int bh = Math.Max(10, (int)Math.Round(box.Height));

                    // ── Bounding box (3px thick outline) ────────
                    filters.Add(
                        $"drawbox=x={bx}:y={by}:w={bw}:h={bh}" +
                        $":color={color}@0.85:t=3:{enableExpr}");

                    if (!includeText) continue;

                    // ── Label text ──────────────────────────────
                    int confPct = (int)(det.Confidence * 100);
                    string labelRaw = $"#{trackId} {det.Problem} {confPct}%%";
                    string label = SanitizeForDrawtext(labelRaw);

                    // Calculate label position (above box, clamped to frame)
                    int lblH = 24;
                    int lblW = label.Length * 9 + 12;

                    // If box is near top, put label BELOW top edge of box
                    int lblY = by > lblH + 4 ? by - lblH - 2 : by + 4;
                    int lblX = bx;

                    // Label background
                    filters.Add(
                        $"drawbox=x={lblX}:y={lblY}:w={lblW}:h={lblH}" +
                        $":color=black@0.78:t=fill:{enableExpr}");

                    // Colored top border on label
                    filters.Add(
                        $"drawbox=x={lblX}:y={lblY}:w={lblW}:h=2" +
                        $":color={color}@0.95:t=fill:{enableExpr}");

                    // Text
                    string fontPart = fontClause != null ? $":{fontClause}" : "";
                    filters.Add(
                        $"drawtext=text='{label}'" +
                        $":x={lblX + 6}:y={lblY + 5}" +
                        $":fontsize=14:fontcolor=white" +
                        $"{fontPart}:{enableExpr}");
                }

                // ── Frame info overlay (top-left corner) ────────
                if (includeText)
                {
                    string frameText = SanitizeForDrawtext(
                        $"Frame {frame.FrameNumber} | {frame.Timestamp}");

                    filters.Add(
                        $"drawbox=x=6:y=6:w=210:h=28" +
                        $":color=black@0.7:t=fill:{enableExpr}");

                    string ftFont = fontClause != null ? $":{fontClause}" : "";
                    filters.Add(
                        $"drawtext=text='{frameText}'" +
                        $":x=12:y=11:fontsize=13" +
                        $":fontcolor=0x00D4FF{ftFont}:{enableExpr}");

                    // Defect count (top-right)
                    string countText = SanitizeForDrawtext(
                        $"{frame.DefectsInFrame} defect(s)");

                    filters.Add(
                        $"drawbox=x=iw-160:y=6:w=150:h=28" +
                        $":color=red@0.8:t=fill:{enableExpr}");

                    filters.Add(
                        $"drawtext=text='{countText}'" +
                        $":x=iw-152:y=11:fontsize=13" +
                        $":fontcolor=white{ftFont}:{enableExpr}");
                }
            }

            if (filters.Count == 0) return string.Empty;
            return string.Join(",\n", filters);
        }

        /// <summary>
        /// Sanitizes text for FFmpeg drawtext filter.
        /// FFmpeg drawtext has specific escaping requirements:
        /// - : must be \: (but we handle this differently in filter_script)
        /// - ' cannot appear inside single-quoted text
        /// - % is used for time codes, so %% means literal %
        /// - [ ] have special meaning in some contexts
        /// </summary>
        private static string SanitizeForDrawtext(string text)
        {
            if (string.IsNullOrEmpty(text)) return "detection";

            var sb = new StringBuilder(text.Length + 10);
            foreach (char c in text)
            {
                switch (c)
                {
                    case '\'': break; // Remove single quotes
                    case '"': break;  // Remove double quotes
                    case '\\': break; // Remove backslashes
                    case ':': sb.Append("\\:"); break; // Escape colons
                    case ';': sb.Append(' '); break;
                    case '[': sb.Append('('); break;
                    case ']': sb.Append(')'); break;
                    case '{': sb.Append('('); break;
                    case '}': sb.Append(')'); break;
                    case '=': sb.Append('-'); break;
                    case ',': sb.Append(' '); break;
                    default: sb.Append(c); break;
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// Finds a usable TrueType font file on the system.
        /// </summary>
        private string? ResolveFontPath()
        {
            var paths = OperatingSystem.IsWindows()
                ? WindowsFontPaths
                : LinuxFontPaths;

            foreach (var path in paths)
            {
                // For Windows paths, convert FFmpeg format back to check existence
                string checkPath = path
                    .Replace("C\\:", "C:")
                    .Replace("/", Path.DirectorySeparatorChar.ToString());

                if (File.Exists(checkPath))
                {
                    _logger.LogDebug("Found font: {Path}", path);
                    return path; // Return FFmpeg-formatted path
                }
            }

            // Try to find any .ttf file
            try
            {
                string fontsDir = OperatingSystem.IsWindows()
                    ? @"C:\Windows\Fonts"
                    : "/usr/share/fonts";

                if (Directory.Exists(fontsDir))
                {
                    var ttf = Directory.GetFiles(fontsDir, "*.ttf",
                        SearchOption.AllDirectories).FirstOrDefault();

                    if (ttf != null)
                    {
                        // Convert to FFmpeg path format
                        string ffmpegPath = ttf
                            .Replace("\\", "/")
                            .Replace("C:", "C\\:");

                        _logger.LogDebug("Found fallback font: {Path}", ffmpegPath);
                        return ffmpegPath;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Font search failed.");
            }

            return null;
        }

        private async Task<bool> CopyVideoAsync(string input, string output)
        {
            var (code, _, _) = await RunFFmpegAsync(
                $"-y -i \"{input}\" -c copy \"{output}\"", 120);
            return File.Exists(output) && new FileInfo(output).Length > 0;
        }

        private async Task<(int exitCode, string stdout, string stderr)>
            RunFFmpegAsync(string arguments, int timeoutSec)
        {
            var psi = new ProcessStartInfo
            {
                FileName = _ffmpegPath,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = new Process { StartInfo = psi };

            try { proc.Start(); }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start FFmpeg.");
                return (-1, "", ex.Message);
            }

            var soTask = proc.StandardOutput.ReadToEndAsync();
            var seTask = proc.StandardError.ReadToEndAsync();

            bool exited = await Task.Run(() =>
                proc.WaitForExit(timeoutSec * 1000));

            if (!exited)
            {
                try { proc.Kill(true); } catch { }
                return (-1, "", "Timeout");
            }

            return (proc.ExitCode, await soTask, await seTask);
        }
    }
}