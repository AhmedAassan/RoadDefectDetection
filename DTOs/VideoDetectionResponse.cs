using RoadDefectDetection.Services;

namespace RoadDefectDetection.DTOs
{
    public class VideoDetectionResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
        public string VideoName { get; set; } = string.Empty;

        // ── Video Metadata ──────────────────────────────────────
        public double VideoDurationSeconds { get; set; }
        public string VideoDuration { get; set; } = string.Empty;
        public double VideoFps { get; set; }
        public int VideoWidth { get; set; }
        public int VideoHeight { get; set; }
        public long TotalFrameCount { get; set; }

        // ── Processing Stats ────────────────────────────────────
        public int FramesAnalyzed { get; set; }
        public int FrameIntervalUsed { get; set; }
        public double ProcessingTimeMs { get; set; }
        public double AvgTimePerFrameMs { get; set; }

        // ── Detection Summary ───────────────────────────────────
        public int TotalRawDetections { get; set; }
        public int TotalUniqueDefects { get; set; }
        public int FramesWithDefects { get; set; }
        public Dictionary<string, int> DefectSummary { get; set; } = new();
        public Dictionary<string, int> RawDefectSummary { get; set; } = new();

        // ── Annotated Video ─────────────────────────────────────
        /// <summary>
        /// Unique ID to retrieve the annotated video via 
        /// /api/videodetection/annotated/{id}
        /// Only populated when annotated video generation succeeds.
        /// </summary>
        public string? AnnotatedVideoId { get; set; }

        /// <summary>
        /// URL path to download/stream the annotated video.
        /// </summary>
        public string? AnnotatedVideoUrl { get; set; }

        // ── Tracking Results ────────────────────────────────────
        public List<UniqueDefectSummary> UniqueDefects { get; set; } = new();

        // ── Per-Frame Results ───────────────────────────────────
        public List<FrameDetectionResult> FrameResults { get; set; } = new();

        public int TotalDefectsFound
        {
            get => TotalUniqueDefects;
            set => TotalUniqueDefects = value;
        }
    }
}