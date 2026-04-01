using RoadDefectDetection.DTOs;

namespace RoadDefectDetection.Services
{
    /// <summary>
    /// IoU-based multi-frame object tracker with Exponential Moving Average (EMA)
    /// smoothing to eliminate bounding box jitter between frames.
    /// 
    /// Improvements over basic tracker:
    /// 1. EMA smoothing: box positions are smoothed over time
    /// 2. Velocity estimation: predicts where a box will be in the next frame
    /// 3. Better matching: uses predicted position for IoU computation
    /// 4. Stable output: smoothed boxes don't jump between frames
    /// </summary>
    public class SimpleTracker
    {
        private readonly List<TrackedDefect> _allTracks = new();
        private int _nextTrackId = 1;
        private readonly float _iouThreshold;
        private readonly int _maxFramesLost;

        /// <summary>
        /// EMA smoothing factor (0 to 1).
        /// Lower = smoother (more historical weight) but slower to react.
        /// Higher = more responsive but more jittery.
        /// 0.4 is a good balance for road defect detection.
        /// </summary>
        private const float SmoothingAlpha = 0.4f;

        public IReadOnlyList<TrackedDefect> AllTracks => _allTracks;
        public int ActiveTrackCount => _allTracks.Count(t => !t.IsLost);
        public int TotalUniqueDefects => _allTracks.Count;

        public SimpleTracker(float iouThreshold = 0.25f, int maxFramesLost = 5)
        {
            _iouThreshold = iouThreshold;
            _maxFramesLost = maxFramesLost;
        }

        /// <summary>
        /// Processes detections from a single frame with EMA smoothing.
        /// Returns matches with SMOOTHED bounding boxes (not raw detections).
        /// </summary>
        public List<TrackMatch> Update(
            List<DetectionResult> detections,
            int frameNumber,
            double timestampSeconds)
        {
            var matches = new List<TrackMatch>();

            if (detections == null || detections.Count == 0)
            {
                foreach (var track in _allTracks.Where(t => !t.IsLost))
                {
                    track.FramesSinceLastSeen++;
                    if (track.FramesSinceLastSeen > _maxFramesLost)
                        track.IsLost = true;
                }
                return matches;
            }

            var activeTracks = _allTracks.Where(t => !t.IsLost).ToList();
            var matchedDetIdx = new HashSet<int>();
            var matchedTrackIdx = new HashSet<int>();

            // ── Step 1: Predict where each track should be ──────
            // Use velocity to predict position (helps matching)
            foreach (var track in activeTracks)
            {
                track.PredictedBox = PredictNextPosition(track);
            }

            // ── Step 2: Build IoU matrix using PREDICTED positions ─
            var iouPairs = new List<(int trackIdx, int detIdx, float iou)>();

            for (int t = 0; t < activeTracks.Count; t++)
            {
                for (int d = 0; d < detections.Count; d++)
                {
                    if (activeTracks[t].DefectClass != detections[d].Problem)
                        continue;

                    // Use predicted position for better matching
                    BoundingBox trackBox = activeTracks[t].PredictedBox
                        ?? activeTracks[t].SmoothedBox;

                    float iou = ComputeIoU(trackBox, detections[d].Box);
                    if (iou >= _iouThreshold)
                    {
                        iouPairs.Add((t, d, iou));
                    }
                }
            }

            // ── Step 3: Greedy matching (highest IoU first) ─────
            foreach (var pair in iouPairs.OrderByDescending(p => p.iou))
            {
                if (matchedTrackIdx.Contains(pair.trackIdx) ||
                    matchedDetIdx.Contains(pair.detIdx))
                    continue;

                var track = activeTracks[pair.trackIdx];
                var detection = detections[pair.detIdx];

                // ── Apply EMA smoothing to box coordinates ──────
                var rawBox = detection.Box;
                var prevSmooth = track.SmoothedBox;

                var smoothedBox = new BoundingBox
                {
                    X = Lerp(prevSmooth.X, rawBox.X, SmoothingAlpha),
                    Y = Lerp(prevSmooth.Y, rawBox.Y, SmoothingAlpha),
                    Width = Lerp(prevSmooth.Width, rawBox.Width, SmoothingAlpha),
                    Height = Lerp(prevSmooth.Height, rawBox.Height, SmoothingAlpha)
                };

                // ── Update velocity estimate ────────────────────
                track.VelocityX = rawBox.X - prevSmooth.X;
                track.VelocityY = rawBox.Y - prevSmooth.Y;
                track.VelocityW = rawBox.Width - prevSmooth.Width;
                track.VelocityH = rawBox.Height - prevSmooth.Height;

                // ── Update track state ──────────────────────────
                track.SmoothedBox = smoothedBox;
                track.RawBox = rawBox;
                track.LastConfidence = detection.Confidence;
                track.LastFrameNumber = frameNumber;
                track.LastTimestamp = timestampSeconds;
                track.FramesSinceLastSeen = 0;
                track.TotalAppearances++;

                if (detection.Confidence > track.BestConfidence)
                {
                    track.BestConfidence = detection.Confidence;
                    track.BestBox = rawBox;
                    track.BestFrameNumber = frameNumber;
                    track.BestTimestamp = timestampSeconds;
                }

                // Return SMOOTHED detection (not raw)
                var smoothedDetection = new DetectionResult
                {
                    Problem = detection.Problem,
                    Confidence = detection.Confidence,
                    ModelSource = detection.ModelSource,
                    Box = smoothedBox
                };

                matches.Add(new TrackMatch
                {
                    TrackId = track.TrackId,
                    Detection = smoothedDetection,
                    RawDetection = detection,
                    IsNewTrack = false
                });

                matchedTrackIdx.Add(pair.trackIdx);
                matchedDetIdx.Add(pair.detIdx);
            }

            // ── Step 4: New tracks for unmatched detections ─────
            for (int d = 0; d < detections.Count; d++)
            {
                if (matchedDetIdx.Contains(d)) continue;

                var det = detections[d];
                var newTrack = new TrackedDefect
                {
                    TrackId = _nextTrackId++,
                    DefectClass = det.Problem,
                    ModelSource = det.ModelSource,
                    FirstFrameNumber = frameNumber,
                    FirstTimestamp = timestampSeconds,
                    LastFrameNumber = frameNumber,
                    LastTimestamp = timestampSeconds,
                    SmoothedBox = det.Box,
                    RawBox = det.Box,
                    LastConfidence = det.Confidence,
                    BestBox = det.Box,
                    BestConfidence = det.Confidence,
                    BestFrameNumber = frameNumber,
                    BestTimestamp = timestampSeconds,
                    TotalAppearances = 1,
                    FramesSinceLastSeen = 0,
                    IsLost = false,
                    VelocityX = 0,
                    VelocityY = 0,
                    VelocityW = 0,
                    VelocityH = 0
                };

                _allTracks.Add(newTrack);

                matches.Add(new TrackMatch
                {
                    TrackId = newTrack.TrackId,
                    Detection = det,
                    RawDetection = det,
                    IsNewTrack = true
                });
            }

            // ── Step 5: Age unmatched tracks ────────────────────
            for (int t = 0; t < activeTracks.Count; t++)
            {
                if (matchedTrackIdx.Contains(t)) continue;
                activeTracks[t].FramesSinceLastSeen++;
                if (activeTracks[t].FramesSinceLastSeen > _maxFramesLost)
                    activeTracks[t].IsLost = true;
            }

            return matches;
        }

        /// <summary>
        /// Predicts the next position of a track based on its velocity.
        /// Simple linear prediction.
        /// </summary>
        private BoundingBox? PredictNextPosition(TrackedDefect track)
        {
            // Only predict if we have velocity data and track was recently seen
            if (track.TotalAppearances < 2 || track.FramesSinceLastSeen > 2)
                return null;

            return new BoundingBox
            {
                X = track.SmoothedBox.X + track.VelocityX * 0.5f,
                Y = track.SmoothedBox.Y + track.VelocityY * 0.5f,
                Width = Math.Max(10, track.SmoothedBox.Width + track.VelocityW * 0.3f),
                Height = Math.Max(10, track.SmoothedBox.Height + track.VelocityH * 0.3f)
            };
        }

        /// <summary>
        /// Linear interpolation (used for EMA smoothing).
        /// </summary>
        private static float Lerp(float previous, float current, float alpha)
        {
            return previous + alpha * (current - previous);
        }

        public List<UniqueDefectSummary> GetUniqueDefectSummary()
        {
            return _allTracks.Select(t => new UniqueDefectSummary
            {
                TrackId = t.TrackId,
                DefectClass = t.DefectClass,
                BestConfidence = t.BestConfidence,
                BestBoundingBox = t.BestBox,
                BestFrameNumber = t.BestFrameNumber,
                BestTimestamp = t.BestTimestamp,
                FirstSeenFrame = t.FirstFrameNumber,
                FirstSeenTimestamp = t.FirstTimestamp,
                LastSeenFrame = t.LastFrameNumber,
                LastSeenTimestamp = t.LastTimestamp,
                TotalFrameAppearances = t.TotalAppearances,
                ModelSource = t.ModelSource
            }).ToList();
        }

        public void Reset()
        {
            _allTracks.Clear();
            _nextTrackId = 1;
        }

        private static float ComputeIoU(BoundingBox a, BoundingBox b)
        {
            float x1 = Math.Max(a.X, b.X);
            float y1 = Math.Max(a.Y, b.Y);
            float x2 = Math.Min(a.X + a.Width, b.X + b.Width);
            float y2 = Math.Min(a.Y + a.Height, b.Y + b.Height);

            float iW = Math.Max(0, x2 - x1);
            float iH = Math.Max(0, y2 - y1);
            float iArea = iW * iH;

            float aA = a.Width * a.Height;
            float aB = b.Width * b.Height;
            float uArea = aA + aB - iArea;

            return uArea <= 0 ? 0f : iArea / uArea;
        }
    }

    /// <summary>
    /// Tracked defect with smoothing state.
    /// </summary>
    public class TrackedDefect
    {
        public int TrackId { get; set; }
        public string DefectClass { get; set; } = string.Empty;
        public string ModelSource { get; set; } = string.Empty;

        // First appearance
        public int FirstFrameNumber { get; set; }
        public double FirstTimestamp { get; set; }

        // Last appearance
        public int LastFrameNumber { get; set; }
        public double LastTimestamp { get; set; }
        public float LastConfidence { get; set; }

        // Smoothed box (EMA-filtered — use this for rendering)
        public BoundingBox SmoothedBox { get; set; } = new();

        // Raw box (unfiltered — latest detection)
        public BoundingBox RawBox { get; set; } = new();

        // Predicted position for next frame
        public BoundingBox? PredictedBox { get; set; }

        // Velocity estimates (pixels per frame)
        public float VelocityX { get; set; }
        public float VelocityY { get; set; }
        public float VelocityW { get; set; }
        public float VelocityH { get; set; }

        // Best detection
        public BoundingBox BestBox { get; set; } = new();
        public float BestConfidence { get; set; }
        public int BestFrameNumber { get; set; }
        public double BestTimestamp { get; set; }

        // State
        public int TotalAppearances { get; set; }
        public int FramesSinceLastSeen { get; set; }
        public bool IsLost { get; set; }
    }

    public class TrackMatch
    {
        public int TrackId { get; set; }
        /// <summary>Smoothed detection (use for rendering)</summary>
        public DetectionResult Detection { get; set; } = new();
        /// <summary>Raw detection from model (unsmoothed)</summary>
        public DetectionResult RawDetection { get; set; } = new();
        public bool IsNewTrack { get; set; }
    }

    public class UniqueDefectSummary
    {
        public int TrackId { get; set; }
        public string DefectClass { get; set; } = string.Empty;
        public float BestConfidence { get; set; }
        public BoundingBox BestBoundingBox { get; set; } = new();
        public int BestFrameNumber { get; set; }
        public double BestTimestamp { get; set; }
        public int FirstSeenFrame { get; set; }
        public double FirstSeenTimestamp { get; set; }
        public int LastSeenFrame { get; set; }
        public double LastSeenTimestamp { get; set; }
        public int TotalFrameAppearances { get; set; }
        public string ModelSource { get; set; } = string.Empty;
    }
}