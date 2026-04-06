namespace RoadDefectDetection.DTOs
{
    /// <summary>
    /// Parameters controlling how a video is analyzed.
    /// </summary>
    public class VideoDetectionRequest
    {
        /// <summary>
        /// Analyze every Nth source frame.
        /// Default: 30 (≈1 frame/second at 30fps).
        /// </summary>
        public int FrameInterval { get; set; } = 30;

        /// <summary>
        /// Optional confidence threshold (0–100).
        /// </summary>
        public float? Confidence { get; set; }

        /// <summary>
        /// Maximum number of frames to analyze regardless of video length.
        /// </summary>
        public int MaxFrames { get; set; } = 120;
    }
}