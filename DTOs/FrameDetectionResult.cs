namespace RoadDefectDetection.DTOs
{
    /// <summary>
    /// Detection results for a single video frame.
    /// Now includes track IDs for each detection.
    /// </summary>
    public class FrameDetectionResult
    {
        public int FrameNumber { get; set; }
        public double TimestampSeconds { get; set; }
        public string Timestamp { get; set; } = string.Empty;
        public int DefectsInFrame { get; set; }
        public List<DetectionResult> Detections { get; set; } = new();

        /// <summary>
        /// Track IDs for each detection in this frame.
        /// Index matches the Detections list.
        /// Allows the UI to color-code or label tracked defects.
        /// </summary>
        public List<int> TrackIds { get; set; } = new();
    }
}