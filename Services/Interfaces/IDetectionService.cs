using RoadDefectDetection.DTOs;

namespace RoadDefectDetection.Services.Interfaces
{
    /// <summary>
    /// Orchestrates defect detection across all loaded ONNX models.
    /// Implementations should load models at startup and reuse inference sessions.
    /// </summary>
    public interface IDetectionService
    {
        /// <summary>
        /// Analyzes a single image for road defects using all enabled models.
        /// </summary>
        /// <param name="imageBytes">Raw bytes of the image file (JPEG, PNG, etc.).</param>
        /// <param name="imageName">Original file name for tracking purposes.</param>
        /// <returns>A <see cref="DetectionResponse"/> containing all detected defects.</returns>
        Task<DetectionResponse> DetectAsync(byte[] imageBytes, string imageName, float? confidenceThreshold = null);

        /// <summary>
        /// Analyzes multiple images in batch. Each image is processed independently.
        /// </summary>
        /// <param name="images">List of tuples containing image bytes and file names.</param>
        /// <returns>A list of <see cref="DetectionResponse"/>, one per input image.</returns>
        Task<List<DetectionResponse>> DetectMultipleAsync(List<(byte[] bytes, string name)> images, float? confidenceThreshold = null);

        /// <summary>
        /// Returns metadata about all currently loaded models (name, class count, enabled status).
        /// Useful for health-check and admin endpoints.
        /// </summary>
        /// <returns>A list of anonymous objects describing each model.</returns>
        List<object> GetLoadedModels();

        /// <summary>
        /// Quick health check — returns true if at least one model is loaded and operational.
        /// </summary>
        /// <returns>True if the service can process images; false otherwise.</returns>
        bool IsHealthy();
    }
}