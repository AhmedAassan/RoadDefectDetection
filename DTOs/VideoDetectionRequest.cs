namespace RoadDefectDetection.DTOs
{
    /// <summary>
    /// Parameters controlling how a video is analyzed.
    /// Sent as form fields alongside the video file upload.
    /// </summary>
    public class VideoDetectionRequest
    {
        /// <summary>
        /// Analyze every Nth frame. For example, if the video is 30fps
        /// and FrameInterval=30, one frame per second is analyzed.
        /// Default: 30 (approximately 1 frame per second for 30fps video).
        /// </summary>
        public int FrameInterval { get; set; } = 30;

        /// <summary>
        /// Optional confidence threshold (0-100). 
        /// If not provided, uses the model's default.
        /// </summary>
        public float? Confidence { get; set; }

        /// <summary>
        /// Maximum number of frames to analyze regardless of video length.
        /// Prevents excessively long processing on very long videos.
        /// Default: 120 frames.
        /// </summary>
        public int MaxFrames { get; set; } = 120;
    }
}