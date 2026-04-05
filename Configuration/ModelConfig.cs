namespace RoadDefectDetection.Configuration
{
    /// <summary>
    /// Global detection settings that apply across all models.
    /// Bound from the "DetectionSettings" section of appsettings.json.
    /// </summary>
    public class DetectionSettings
    {
        /// <summary>
        /// Default confidence threshold used when a model does not specify its own.
        /// Intentionally low (0.15) to catch more potential defects.
        /// </summary>
        public float DefaultConfidenceThreshold { get; set; } = 0.15f;

        /// <summary>
        /// Intersection-over-Union threshold for Non-Maximum Suppression.
        /// Detections with IoU above this value are considered duplicates.
        /// </summary>
        public float IouThreshold { get; set; } = 0.45f;

        /// <summary>
        /// The square input dimension expected by all YOLO models (e.g., 640 means 640×640).
        /// </summary>
        public int InputSize { get; set; } = 640;

        /// <summary>
        /// Maximum allowed image file size in megabytes.
        /// Images exceeding this limit will be rejected before processing.
        /// </summary>
        public int MaxImageSizeMB { get; set; } = 50;
    }

    /// <summary>
    /// Configuration for a single ONNX detection model.
    /// Bound from the "Models" array in appsettings.json.
    /// To add a new model, drop the .onnx file into the models folder
    /// and add a new entry to this array — no code changes required.
    /// </summary>
    public class ModelConfig
    {
        /// <summary>
        /// Human-readable name for this model (e.g., "Road Surface Defects").
        /// Used in detection results to identify which model produced a finding.
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Unique numeric identifier for this model.
        /// Used by the external system mapping to link internal class indices
        /// to external class IDs. Must be unique across all models.
        /// Example: Model 1 = "Road Surface Defects", Model 2 = "Trash Bin Detection".
        /// </summary>
        public int ModelId { get; set; }

        /// <summary>
        /// The ONNX file name located in the models directory (e.g., "model1_road_defects.onnx").
        /// </summary>
        public string FileName { get; set; } = string.Empty;

        /// <summary>
        /// Whether this model should be loaded and used for detection.
        /// Set to false to disable a model without removing its configuration.
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Model-specific confidence threshold. Overrides the global DefaultConfidenceThreshold.
        /// </summary>
        public float ConfidenceThreshold { get; set; } = 0.15f;

        /// <summary>
        /// Ordered array of class names the model can detect.
        /// The index must match the class index in the ONNX model output.
        /// </summary>
        public string[] Classes { get; set; } = Array.Empty<string>();
    }

    /// <summary>
    /// Maps an internal model class to the external system's class ID and name.
    /// 
    /// The external system (UI) has its own numbering scheme for defect types.
    /// This mapping bridges:
    ///   Internal: (ModelId=1, ClassIndex=2) → "Pothole" (from model output)
    ///   External: (ExternalClassId=1)       → "Potholes" (in external UI)
    /// 
    /// To add new mappings, simply add entries to the "ExternalClassMappings"
    /// array in appsettings.json — no code changes required.
    /// </summary>
    public class ExternalClassMapping
    {
        /// <summary>
        /// The class ID used by the external system / UI.
        /// This is the number the external system expects to receive.
        /// Example: 1 = "Potholes", 2 = "Alligator Cracking", etc.
        /// </summary>
        public int ExternalClassId { get; set; }

        /// <summary>
        /// The class name used by the external system / UI.
        /// May differ from the internal model's class name.
        /// Example: Internal "Alligator Crack" → External "Alligator Cracking".
        /// </summary>
        public string ExternalClassName { get; set; } = string.Empty;

        /// <summary>
        /// The class index within the specific model's output tensor.
        /// This matches the index in the ModelConfig.Classes array.
        /// Example: In Model 1, index 0 = "Alligator Crack", index 2 = "Pothole".
        /// </summary>
        public int InternalModelClassIndex { get; set; }

        /// <summary>
        /// The ModelId this mapping belongs to (matches ModelConfig.ModelId).
        /// Combined with InternalModelClassIndex, this uniquely identifies
        /// the source of a detection.
        /// </summary>
        public int ModelId { get; set; }

        /// <summary>
        /// Optional severity level for the external system.
        /// Allows the external UI to color-code or prioritize defects.
        /// Examples: "Critical", "High", "Medium", "Low".
        /// </summary>
        public string Severity { get; set; } = string.Empty;

        /// <summary>
        /// Optional category grouping for the external system.
        /// Allows the external UI to group related defect types.
        /// Examples: "Road Surface", "Street Cleanliness", "Infrastructure".
        /// </summary>
        public string Category { get; set; } = string.Empty;
    }
}