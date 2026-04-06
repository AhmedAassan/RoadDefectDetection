namespace RoadDefectDetection.Configuration
{
    /// <summary>
    /// Global detection settings. Bound from "DetectionSettings" in appsettings.json.
    /// </summary>
    public class DetectionSettings
    {
        public float DefaultConfidenceThreshold { get; set; } = 0.15f;
        public float IouThreshold { get; set; } = 0.45f;
        public int InputSize { get; set; } = 640;
        public int MaxImageSizeMB { get; set; } = 50;

        /// <summary>
        /// Maximum number of images processed in parallel during batch detection.
        /// </summary>
        public int MaxConcurrentDetections { get; set; } = 2;
    }

    /// <summary>
    /// Configuration for a single ONNX detection model.
    /// </summary>
    public class ModelConfig
    {
        public string Name { get; set; } = string.Empty;
        public int ModelId { get; set; }
        public string FileName { get; set; } = string.Empty;
        public bool Enabled { get; set; } = true;
        public float ConfidenceThreshold { get; set; } = 0.15f;
        public string[] Classes { get; set; } = Array.Empty<string>();
    }

    /// <summary>
    /// Maps an internal model class to the external system's class ID and name.
    /// </summary>
    public class ExternalClassMapping
    {
        public int ExternalClassId { get; set; }
        public string ExternalClassName { get; set; } = string.Empty;
        public int InternalModelClassIndex { get; set; }
        public int ModelId { get; set; }
        public string Severity { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
    }
}