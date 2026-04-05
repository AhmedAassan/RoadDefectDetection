namespace RoadDefectDetection.DTOs
{
    /// <summary>
    /// Extended detection result that includes the external system mapping.
    /// This is what gets sent to the external system / UI.
    /// 
    /// Contains both the internal detection data (for debugging/logging)
    /// and the external mapping (for the external system to consume).
    /// </summary>
    public class MappedDetectionResult
    {
        // ── Internal Detection Data ─────────────────────────────

        /// <summary>
        /// The internal class name from the model (e.g., "Pothole").
        /// </summary>
        public string InternalClassName { get; set; } = string.Empty;

        /// <summary>
        /// The internal class index from the model output tensor.
        /// </summary>
        public int InternalClassIndex { get; set; }

        /// <summary>
        /// The ModelId that produced this detection.
        /// </summary>
        public int ModelId { get; set; }

        /// <summary>
        /// The model's confidence score for this detection (0.0 to 1.0).
        /// </summary>
        public float Confidence { get; set; }

        /// <summary>
        /// The name of the model that produced this detection.
        /// </summary>
        public string ModelSource { get; set; } = string.Empty;

        /// <summary>
        /// Bounding box in original image pixel space.
        /// </summary>
        public BoundingBox Box { get; set; } = new();

        // ── External System Mapping ─────────────────────────────

        /// <summary>
        /// The class ID expected by the external system.
        /// Null if no mapping exists for this detection.
        /// </summary>
        public int? ExternalClassId { get; set; }

        /// <summary>
        /// The class name expected by the external system.
        /// Null if no mapping exists for this detection.
        /// </summary>
        public string? ExternalClassName { get; set; }

        /// <summary>
        /// Severity level from the mapping configuration.
        /// </summary>
        public string? Severity { get; set; }

        /// <summary>
        /// Category grouping from the mapping configuration.
        /// </summary>
        public string? Category { get; set; }

        /// <summary>
        /// Whether this detection has a valid external mapping.
        /// Detections without mappings can still be returned but
        /// the external system may not know how to display them.
        /// </summary>
        public bool HasExternalMapping => ExternalClassId.HasValue;
    }

    /// <summary>
    /// Response containing mapped detection results for the external system.
    /// Wraps the standard DetectionResponse with additional mapping information.
    /// </summary>
    public class MappedDetectionResponse
    {
        /// <summary>
        /// Indicates whether the detection pipeline completed without errors.
        /// </summary>
        public bool Success { get; set; }

        /// <summary>
        /// Human-readable summary message.
        /// </summary>
        public string Message { get; set; } = string.Empty;

        /// <summary>
        /// The original file name of the analyzed image.
        /// </summary>
        public string ImageName { get; set; } = string.Empty;

        /// <summary>
        /// Total number of defects found after NMS filtering.
        /// </summary>
        public int TotalProblemsFound { get; set; }

        /// <summary>
        /// Number of detections that have valid external system mappings.
        /// </summary>
        public int MappedDetections { get; set; }

        /// <summary>
        /// Number of detections without external system mappings.
        /// These are still valid detections but the external system
        /// may not have a corresponding class definition.
        /// </summary>
        public int UnmappedDetections { get; set; }

        /// <summary>
        /// Processing time in milliseconds.
        /// </summary>
        public double ProcessingTimeMs { get; set; }

        /// <summary>
        /// The list of mapped detection results.
        /// </summary>
        public List<MappedDetectionResult> Detections { get; set; } = new();
    }
}