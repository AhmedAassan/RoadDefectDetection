using RoadDefectDetection.DTOs;

namespace RoadDefectDetection.Services.Interfaces
{
    /// <summary>
    /// Handles video-based road defect detection by extracting frames
    /// and running the image detection pipeline on each selected frame.
    /// </summary>
    public interface IVideoDetectionService
    {
        /// <summary>
        /// Analyzes a video file for road defects by extracting and 
        /// processing frames at the specified interval.
        /// </summary>
        /// <param name="videoBytes">Raw bytes of the video file.</param>
        /// <param name="videoName">Original file name for tracking.</param>
        /// <param name="request">Processing parameters (frame interval, 
        /// confidence, max frames).</param>
        /// <param name="progress">Optional callback invoked after each 
        /// frame is processed. Receives (currentFrame, totalFrames).</param>
        /// <returns>A <see cref="VideoDetectionResponse"/> with all 
        /// frame-level and summary results.</returns>
        Task<VideoDetectionResponse> DetectVideoAsync(
            byte[] videoBytes,
            string videoName,
            VideoDetectionRequest request,
            Action<int, int>? progress = null);

        /// <summary>
        /// Returns the list of video file extensions this service supports.
        /// </summary>
        string[] SupportedExtensions { get; }

        /// <summary>
        /// Checks whether FFmpeg is available and the service can 
        /// process videos.
        /// </summary>
        Task<bool> IsAvailableAsync();
    }
}