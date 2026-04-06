namespace RoadDefectDetection.DTOs
{
    /// <summary>
    /// Extended detection result including the external system mapping.
    /// </summary>
    public class MappedDetectionResult
    {
        public string InternalClassName { get; set; } = string.Empty;
        public int InternalClassIndex { get; set; }
        public int ModelId { get; set; }
        public float Confidence { get; set; }
        public string ModelSource { get; set; } = string.Empty;
        public BoundingBox Box { get; set; } = new();

        public int? ExternalClassId { get; set; }
        public string? ExternalClassName { get; set; }
        public string? Severity { get; set; }
        public string? Category { get; set; }

        public bool HasExternalMapping => ExternalClassId.HasValue;
    }

    /// <summary>
    /// Response containing mapped detection results for the external system.
    /// </summary>
    public class MappedDetectionResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
        public string ImageName { get; set; } = string.Empty;
        public int TotalProblemsFound { get; set; }
        public int MappedDetections { get; set; }
        public int UnmappedDetections { get; set; }
        public double ProcessingTimeMs { get; set; }
        public List<MappedDetectionResult> Detections { get; set; } = new();
    }
}