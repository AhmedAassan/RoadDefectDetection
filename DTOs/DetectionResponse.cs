namespace RoadDefectDetection.DTOs
{
    /// <summary>
    /// The complete response returned after analyzing a single image.
    /// Aggregates detections from all enabled models.
    /// </summary>
    public class DetectionResponse
    {
        /// <summary>
        /// Indicates whether the detection pipeline completed without errors.
        /// </summary>
        public bool Success { get; set; }

        /// <summary>
        /// A human-readable summary message (e.g., "Detection completed" or an error description).
        /// </summary>
        public string Message { get; set; } = string.Empty;

        /// <summary>
        /// The original file name of the analyzed image.
        /// </summary>
        public string ImageName { get; set; } = string.Empty;

        /// <summary>
        /// Total number of defects found across all models after NMS filtering.
        /// </summary>
        public int TotalProblemsFound { get; set; }

        /// <summary>
        /// Wall-clock time in milliseconds taken to process this image
        /// (including all models, preprocessing, and NMS).
        /// </summary>
        public double ProcessingTimeMs { get; set; }

        /// <summary>
        /// The list of individual detections. Empty if no defects were found.
        /// </summary>
        public List<DetectionResult> Detections { get; set; } = new();
    }
}