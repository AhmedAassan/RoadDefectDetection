using OpenCvSharp;
using RoadDefectDetection.DTOs;
using System;

namespace RoadDefectDetection.Services
{
    /// <summary>
    /// Creates annotated output videos using OpenCvSharp drawing primitives.
    /// 
    /// Replaces the previous FFmpeg filter_script approach entirely.
    /// No external processes, no shell escaping, no font-path hunting.
    /// 
    /// Strategy:
    ///   1. Open the original video with VideoCapture.
    ///   2. Create a VideoWriter for the output.
    ///   3. For each source frame, determine which analyzed frame results
    ///      apply (by timestamp bracket).
    ///   4. Draw bounding boxes and text labels directly on the Mat using
    ///      Cv2.Rectangle / Cv2.PutText.
    ///   5. Write the annotated frame to the VideoWriter.
    /// 
    /// Because OpenCvSharp's built-in font (Hershey) does not require any
    /// external font file, text rendering always works on every OS.
    /// </summary>
    public class OpenCvVideoAnnotator
    {
        private readonly ILogger _logger;

        // Color table (BGR — OpenCV uses BGR, not RGB)
        private static readonly Scalar[] BoxColors =
        {
            new Scalar(107, 107, 255),  // red
            new Scalar(136, 255,   0),  // green
            new Scalar(255, 212,   0),  // cyan
            new Scalar(  0, 184, 255),  // yellow
            new Scalar(255,  68, 255),  // magenta
            new Scalar(255, 255, 255),  // white
            new Scalar(  0, 136, 255),  // orange
            new Scalar( 68, 255, 136),  // lime
            new Scalar(255, 136,  68),  // blue
            new Scalar(136,  68, 255),  // pink
        };

        public OpenCvVideoAnnotator(ILogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Reads <paramref name="inputVideoPath"/>, draws annotations for each
        /// frame using the supplied <paramref name="frameResults"/>, and writes
        /// the result to <paramref name="outputVideoPath"/>.
        /// </summary>
        /// <returns>True if the output file was created successfully.</returns>
        public bool CreateAnnotatedVideo(
            string inputVideoPath,
            string outputVideoPath,
            List<FrameDetectionResult> frameResults,
            CancellationToken cancellationToken = default)
        {
            if (frameResults == null || frameResults.Count == 0)
            {
                _logger.LogInformation(
                    "No frame results supplied — copying video without annotation.");
                return CopyWithReEncode(inputVideoPath, outputVideoPath, cancellationToken);
            }

            bool hasAnyDetection = frameResults.Any(f => f.DefectsInFrame > 0);
            if (!hasAnyDetection)
            {
                _logger.LogInformation(
                    "No detections in any frame — copying video without annotation.");
                return CopyWithReEncode(inputVideoPath, outputVideoPath, cancellationToken);
            }

            // Build a lookup: sourceFrameIndex → FrameDetectionResult
            // We match source frames to the nearest analyzed frame bracket.
            var timestampMap = BuildTimestampMap(frameResults);

            using var capture = new VideoCapture(inputVideoPath);
            if (!capture.IsOpened())
            {
                _logger.LogError("OpenCV could not open input video: {Path}", inputVideoPath);
                return false;
            }

            double fps = capture.Get(VideoCaptureProperties.Fps);
            int width = (int)capture.Get(VideoCaptureProperties.FrameWidth);
            int height = (int)capture.Get(VideoCaptureProperties.FrameHeight);
            long total = (long)capture.Get(VideoCaptureProperties.FrameCount);

            if (fps <= 0) fps = 25;

            _logger.LogInformation(
                "Annotating: {W}x{H} @ {Fps:F2}fps, {Total} frames → {Out}",
                width, height, fps, total, outputVideoPath);

            // VideoWriter — try H264 first, fall back to MP4V
            var fourcc = ChooseFourCC(outputVideoPath);
            using var writer = new VideoWriter(
                outputVideoPath, fourcc, fps, new Size(width, height));

            if (!writer.IsOpened())
            {
                _logger.LogError(
                    "OpenCV VideoWriter could not open output path: {Path}", outputVideoPath);
                return false;
            }

            using var frame = new Mat();
            long frameIndex = 0;
            long written = 0;

            while (!cancellationToken.IsCancellationRequested)
            {
                if (!capture.Read(frame) || frame.Empty()) break;

                double timestampSec = frameIndex / fps;
                var result = FindResultForTimestamp(timestampMap, timestampSec);

                if (result != null && result.DefectsInFrame > 0)
                    DrawAnnotations(frame, result, width, height);

                writer.Write(frame);
                frameIndex++;
                written++;

                if (frameIndex % 300 == 0)
                    _logger.LogDebug("Annotated {Done}/{Total} frames.", frameIndex, total);
            }

            _logger.LogInformation(
                "Annotation complete. Wrote {Written} frames.", written);

            return File.Exists(outputVideoPath) &&
                   new FileInfo(outputVideoPath).Length > 0;
        }

        // ── Drawing ──────────────────────────────────────────────

        private void DrawAnnotations(
            Mat frame,
            FrameDetectionResult result,
            int frameWidth,
            int frameHeight)
        {
            for (int di = 0; di < result.Detections.Count; di++)
            {
                var det = result.Detections[di];
                var box = det.Box;
                int trackId = (result.TrackIds != null && di < result.TrackIds.Count)
                    ? result.TrackIds[di] : di + 1;

                var color = BoxColors[(trackId - 1) % BoxColors.Length];

                // Clamp box to frame
                int bx = Math.Max(0, (int)Math.Round(box.X));
                int by = Math.Max(0, (int)Math.Round(box.Y));
                int bw = Math.Max(4, (int)Math.Round(box.Width));
                int bh = Math.Max(4, (int)Math.Round(box.Height));
                bx = Math.Min(bx, frameWidth - 1);
                by = Math.Min(by, frameHeight - 1);
                bw = Math.Min(bw, frameWidth - bx);
                bh = Math.Min(bh, frameHeight - by);

                var rect = new Rect(bx, by, bw, bh);

                // Bounding box (3 px thick)
                Cv2.Rectangle(frame, rect, color, thickness: 3, lineType: LineTypes.AntiAlias);

                // Label
                int confPct = (int)(det.Confidence * 100);
                string label = $"#{trackId} {det.Problem} {confPct}%";

                double fontScale = 0.5;
                int fontThick = 1;
                var fontFace = HersheyFonts.HersheySimplex;

                var textSize = Cv2.GetTextSize(label, fontFace, fontScale, fontThick, out int baseline);

                int lblW = textSize.Width + 10;
                int lblH = textSize.Height + baseline + 8;

                int lblX = bx;
                int lblY = by > lblH + 2 ? by - lblH - 2 : by + 2;

                // Clamp label position
                lblX = Math.Max(0, Math.Min(lblX, frameWidth - lblW));
                lblY = Math.Max(0, Math.Min(lblY, frameHeight - lblH));

                // Label background
                var bgRect = new Rect(lblX, lblY, lblW, lblH);
                Cv2.Rectangle(frame, bgRect, new Scalar(30, 30, 30, 200), thickness: -1);

                // Coloured top stripe on label background
                var stripeRect = new Rect(lblX, lblY, lblW, 3);
                Cv2.Rectangle(frame, stripeRect, color, thickness: -1);

                // Text
                Cv2.PutText(
                    frame, label,
                    new Point(lblX + 5, lblY + lblH - baseline - 4),
                    fontFace, fontScale,
                    new Scalar(255, 255, 255),
                    fontThick, LineTypes.AntiAlias);
            }

            // Frame info overlay (top-left corner)
            string info = $"Frame {result.FrameNumber} | {result.Timestamp}";
            string defInfo = $"{result.DefectsInFrame} defect(s)";

            DrawInfoOverlay(frame, info, new Point(8, 28), frameWidth, frameHeight,
                new Scalar(255, 212, 0));
            DrawInfoOverlay(frame, defInfo, new Point(frameWidth - 160, 28),
                frameWidth, frameHeight, new Scalar(0, 0, 255));
        }

        private static void DrawInfoOverlay(
            Mat frame, string text, Point position,
            int frameWidth, int frameHeight, Scalar color)
        {
            var fontFace = HersheyFonts.HersheySimplex;
            double scale = 0.5;
            int thick = 1;

            var sz = Cv2.GetTextSize(text, fontFace, scale, thick, out int baseline);

            int bgX = Math.Max(0, position.X - 4);
            int bgY = Math.Max(0, position.Y - sz.Height - 4);
            int bgW = Math.Min(sz.Width + 8, frameWidth - bgX);
            int bgH = Math.Min(sz.Height + baseline + 8, frameHeight - bgY);

            Cv2.Rectangle(frame,
                new Rect(bgX, bgY, bgW, bgH),
                new Scalar(20, 20, 20, 180), thickness: -1);

            Cv2.PutText(frame, text, position,
                fontFace, scale, color, thick, LineTypes.AntiAlias);
        }

        // ── Timestamp mapping ─────────────────────────────────────

        /// <summary>
        /// Builds a sorted list of (timestampSeconds, result) pairs for
        /// fast nearest-bracket lookup.
        /// </summary>
        private static List<(double ts, FrameDetectionResult result)> BuildTimestampMap(
            List<FrameDetectionResult> frameResults)
        {
            return frameResults
                .OrderBy(f => f.TimestampSeconds)
                .Select(f => (f.TimestampSeconds, f))
                .ToList();
        }

        /// <summary>
        /// Finds the result whose timestamp bracket contains the given time.
        /// Uses the previous result up until the next result's timestamp.
        /// </summary>
        private static FrameDetectionResult? FindResultForTimestamp(
            List<(double ts, FrameDetectionResult result)> map,
            double timestampSec)
        {
            if (map.Count == 0) return null;

            // Find the last entry whose timestamp is ≤ timestampSec
            FrameDetectionResult? best = null;
            foreach (var (ts, result) in map)
            {
                if (ts <= timestampSec) best = result;
                else break;
            }
            return best;
        }

        // ── Video writer helpers ──────────────────────────────────

        private bool CopyWithReEncode(
            string inputPath, string outputPath,
            CancellationToken cancellationToken)
        {
            try
            {
                using var capture = new VideoCapture(inputPath);
                if (!capture.IsOpened()) return false;

                double fps = capture.Get(VideoCaptureProperties.Fps);
                int width = (int)capture.Get(VideoCaptureProperties.FrameWidth);
                int height = (int)capture.Get(VideoCaptureProperties.FrameHeight);
                if (fps <= 0) fps = 25;

                var fourcc = ChooseFourCC(outputPath);
                using var writer = new VideoWriter(
                    outputPath, fourcc, fps, new Size(width, height));

                if (!writer.IsOpened()) return false;

                using var frame = new Mat();
                while (!cancellationToken.IsCancellationRequested &&
                       capture.Read(frame) && !frame.Empty())
                {
                    writer.Write(frame);
                }

                return File.Exists(outputPath) &&
                       new FileInfo(outputPath).Length > 0;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "CopyWithReEncode failed.");
                return false;
            }
        }

        /// <summary>
        /// Selects the best available FourCC codec for the output file extension.
        /// MP4V works universally with OpenCvSharp on Windows and Linux.
        /// </summary>
        private static int ChooseFourCC(string outputPath)
        {
            string ext = Path.GetExtension(outputPath).ToLowerInvariant();
            return ext switch
            {
                ".mp4" => VideoWriter.FourCC('m', 'p', '4', 'v'),
                ".avi" => VideoWriter.FourCC('X', 'V', 'I', 'D'),
                ".mkv" => VideoWriter.FourCC('X', 'V', 'I', 'D'),
                _ => VideoWriter.FourCC('m', 'p', '4', 'v')
            };
        }
    }
}