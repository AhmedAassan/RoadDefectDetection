namespace RoadDefectDetection.Configuration
{
    /// <summary>
    /// Video processing and cache configuration.
    /// Bound from "VideoProcessing" in appsettings.json.
    /// </summary>
    public class VideoProcessingSettings
    {
        /// <summary>
        /// Maximum video file size accepted by the API in megabytes.
        /// </summary>
        public int MaxVideoSizeMB { get; set; } = 500;

        /// <summary>
        /// Maximum number of annotated videos held in the in-memory cache.
        /// When the limit is reached, the oldest entry is evicted.
        /// </summary>
        public int MaxCacheEntries { get; set; } = 10;

        /// <summary>
        /// Maximum total size of all cached videos in megabytes.
        /// Prevents unbounded RAM consumption on a busy server.
        /// </summary>
        public int MaxCacheTotalSizeMB { get; set; } = 500;

        /// <summary>
        /// How long a cached annotated video lives before expiry (minutes).
        /// </summary>
        public int CacheExpiryMinutes { get; set; } = 30;

        /// <summary>
        /// Default number of source frames to skip between analyzed frames.
        /// </summary>
        public int DefaultFrameInterval { get; set; } = 30;

        /// <summary>
        /// Default maximum number of frames to analyze per video.
        /// </summary>
        public int DefaultMaxFrames { get; set; } = 120;

        /// <summary>
        /// IoU threshold used by the simple object tracker.
        /// </summary>
        public float TrackerIouThreshold { get; set; } = 0.25f;

        /// <summary>
        /// Number of consecutive frames a track can go unmatched before being
        /// marked as lost.
        /// </summary>
        public int TrackerMaxFramesLost { get; set; } = 5;
    }
}