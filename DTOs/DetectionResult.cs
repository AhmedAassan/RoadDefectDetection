namespace RoadDefectDetection.DTOs
{
    /// <summary>
    /// Represents a single detected defect within an image.
    /// </summary>
    public class DetectionResult
    {
        public string Problem { get; set; } = string.Empty;
        public float Confidence { get; set; }
        public string ModelSource { get; set; } = string.Empty;
        public BoundingBox Box { get; set; } = new();
        public int ClassIndex { get; set; }
        public int ModelId { get; set; }
    }

    /// <summary>
    /// Axis-aligned bounding box in original image coordinates.
    /// </summary>
    public class BoundingBox
    {
        public float X { get; set; }
        public float Y { get; set; }
        public float Width { get; set; }
        public float Height { get; set; }
    }
}