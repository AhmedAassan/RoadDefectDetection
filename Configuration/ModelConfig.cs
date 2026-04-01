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
        /// The square input dimension expected by all YOLO models (e.g., 640 means 640x640).
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
}
