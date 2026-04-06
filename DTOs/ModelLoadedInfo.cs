namespace RoadDefectDetection.DTOs
{
    /// <summary>
    /// Describes a single loaded ONNX model.
    /// Replaces the anonymous-type List&lt;object&gt; previously returned by
    /// IDetectionService.GetLoadedModels() with a typed, serialization-safe DTO.
    /// </summary>
    public class ModelLoadedInfo
    {
        public string Name { get; set; } = string.Empty;
        public int ModelId { get; set; }
        public string[] Classes { get; set; } = Array.Empty<string>();
        public int ClassCount { get; set; }
        public string Status { get; set; } = "Loaded";
    }
}