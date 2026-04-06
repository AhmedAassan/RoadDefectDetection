using RoadDefectDetection.DTOs;

namespace RoadDefectDetection.Services.Interfaces
{
    /// <summary>
    /// Handles video-based road defect detection using OpenCV frame extraction.
    /// </summary>
    public interface IVideoDetectionService
    {
        /// <summary>
        /// Analyzes a video file for road defects by processing frames with OpenCV.
        /// </summary>
        /// <param name="videoBytes">Raw bytes of the video file.</param>
        /// <param name="videoName">Original file name for tracking.</param>
        /// <param name="request">Processing parameters.</param>
        /// <param name="progress">Optional progress callback (currentFrame, totalFrames).</param>
        /// <param name="cancellationToken">Cancellation token for long-running operations.</param>
        Task<VideoDetectionResponse> DetectVideoAsync(
            byte[] videoBytes,
            string videoName,
            VideoDetectionRequest request,
            Action<int, int>? progress = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Returns the list of video file extensions this service supports.
        /// </summary>
        string[] SupportedExtensions { get; }

        /// <summary>
        /// Checks whether the OpenCV video backend is available.
        /// This is synchronous because OpenCV is a native library — either
        /// the package is installed or it is not.
        /// </summary>
        bool IsAvailable();
    }
}