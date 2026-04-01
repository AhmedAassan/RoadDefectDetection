namespace RoadDefectDetection.DTOs
{
    /// <summary>
    /// Represents a single detected defect within an image.
    /// </summary>
    public class DetectionResult
    {
        /// <summary>
        /// The class name of the detected defect (e.g., "potholes", "alligator_cracking").
        /// Corresponds to one of the class labels defined in the model configuration.
        /// </summary>
        public string Problem { get; set; } = string.Empty;

        /// <summary>
        /// The model's confidence score for this detection, ranging from 0.0 to 1.0.
        /// A higher value indicates greater certainty that the defect is present.
        /// </summary>
        public float Confidence { get; set; }

        /// <summary>
        /// The name of the model that produced this detection (e.g., "Road Surface Defects").
        /// Useful when multiple models are running to trace which model found what.
        /// </summary>
        public string ModelSource { get; set; } = string.Empty;

        /// <summary>
        /// The bounding box coordinates of the detected defect in original image pixel space.
        /// </summary>
        public BoundingBox Box { get; set; } = new();
    }

    /// <summary>
    /// Axis-aligned bounding box in original image coordinates.
    /// (X, Y) is the top-left corner; Width and Height define the size.
    /// </summary>
    public class BoundingBox
    {
        /// <summary>
        /// X coordinate of the top-left corner in pixels (original image space).
        /// </summary>
        public float X { get; set; }

        /// <summary>
        /// Y coordinate of the top-left corner in pixels (original image space).
        /// </summary>
        public float Y { get; set; }

        /// <summary>
        /// Width of the bounding box in pixels (original image space).
        /// </summary>
        public float Width { get; set; }

        /// <summary>
        /// Height of the bounding box in pixels (original image space).
        /// </summary>
        public float Height { get; set; }
    }
}
