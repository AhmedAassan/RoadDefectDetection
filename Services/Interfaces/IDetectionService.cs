using RoadDefectDetection.DTOs;

namespace RoadDefectDetection.Services.Interfaces
{
    /// <summary>
    /// Orchestrates defect detection across all loaded ONNX models.
    /// </summary>
    public interface IDetectionService
    {
        /// <summary>
        /// Analyzes a single image for road defects using all enabled models.
        /// </summary>
        Task<DetectionResponse> DetectAsync(
            byte[] imageBytes,
            string imageName,
            float? confidenceThreshold = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Analyzes multiple images. Images are processed with bounded parallelism.
        /// </summary>
        Task<List<DetectionResponse>> DetectMultipleAsync(
            List<(byte[] bytes, string name)> images,
            float? confidenceThreshold = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Returns metadata about all currently loaded models.
        /// </summary>
        List<ModelLoadedInfo> GetLoadedModels();

        /// <summary>
        /// Returns true if at least one model is loaded and operational.
        /// </summary>
        bool IsHealthy();
    }
}