namespace RoadDefectDetection.DTOs
{
    /// <summary>
    /// The complete response returned after analyzing a single image.
    /// </summary>
    public class DetectionResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
        public string ImageName { get; set; } = string.Empty;
        public int TotalProblemsFound { get; set; }
        public double ProcessingTimeMs { get; set; }
        public List<DetectionResult> Detections { get; set; } = new();
    }
}