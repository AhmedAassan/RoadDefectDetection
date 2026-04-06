using RoadDefectDetection.DTOs;

namespace RoadDefectDetection.Services
{
    /// <summary>
    /// IoU-based multi-frame object tracker with EMA smoothing and
    /// velocity-based prediction. Preserves ModelId and ClassIndex
    /// so that tracked detections remain mappable to external class IDs.
    /// </summary>
    public class SimpleTracker
    {
        private readonly List<TrackedDefect> _allTracks = new();
        private int _nextTrackId = 1;
        private readonly float _iouThreshold;
        private readonly int _maxFramesLost;

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
        /// Processes detections from one frame. Returns matched/new tracks
        /// with SMOOTHED bounding boxes.
        /// </summary>
        public List<TrackMatch> Update(
            List<DetectionResult> detections,
            int frameNumber,
            double timestampSeconds)
        {
            var matches = new List<TrackMatch>();

            if (detections == null || detections.Count == 0)
            {
                foreach (var t in _allTracks.Where(t => !t.IsLost))
                {
                    t.FramesSinceLastSeen++;
                    if (t.FramesSinceLastSeen > _maxFramesLost) t.IsLost = true;
                }
                return matches;
            }

            var activeTracks = _allTracks.Where(t => !t.IsLost).ToList();
            var matchedDetIdx = new HashSet<int>();
            var matchedTrackIdx = new HashSet<int>();

            // Predict next positions
            foreach (var track in activeTracks)
                track.PredictedBox = PredictNextPosition(track);

            // Build IoU pairs (same class only)
            var iouPairs = new List<(int ti, int di, float iou)>();
            for (int ti = 0; ti < activeTracks.Count; ti++)
            {
                for (int di = 0; di < detections.Count; di++)
                {
                    if (activeTracks[ti].DefectClass != detections[di].Problem) continue;
                    var trackBox = activeTracks[ti].PredictedBox ?? activeTracks[ti].SmoothedBox;
                    float iou = ComputeIoU(trackBox, detections[di].Box);
                    if (iou >= _iouThreshold) iouPairs.Add((ti, di, iou));
                }
            }

            // Greedy matching
            foreach (var (ti, di, _) in iouPairs.OrderByDescending(p => p.iou))
            {
                if (matchedTrackIdx.Contains(ti) || matchedDetIdx.Contains(di)) continue;

                var track = activeTracks[ti];
                var detection = detections[di];
                var rawBox = detection.Box;
                var prev = track.SmoothedBox;

                var smoothed = new BoundingBox
                {
                    X = Lerp(prev.X, rawBox.X, SmoothingAlpha),
                    Y = Lerp(prev.Y, rawBox.Y, SmoothingAlpha),
                    Width = Lerp(prev.Width, rawBox.Width, SmoothingAlpha),
                    Height = Lerp(prev.Height, rawBox.Height, SmoothingAlpha)
                };

                track.VelocityX = rawBox.X - prev.X;
                track.VelocityY = rawBox.Y - prev.Y;
                track.VelocityW = rawBox.Width - prev.Width;
                track.VelocityH = rawBox.Height - prev.Height;

                track.SmoothedBox = smoothed;
                track.RawBox = rawBox;
                track.LastConfidence = detection.Confidence;
                track.LastFrameNumber = frameNumber;
                track.LastTimestamp = timestampSeconds;
                track.FramesSinceLastSeen = 0;
                track.TotalAppearances++;

                // Preserve model identity for mapping
                track.ModelId = detection.ModelId;
                track.ClassIndex = detection.ClassIndex;
                track.ModelSource = detection.ModelSource;

                if (detection.Confidence > track.BestConfidence)
                {
                    track.BestConfidence = detection.Confidence;
                    track.BestBox = rawBox;
                    track.BestFrameNumber = frameNumber;
                    track.BestTimestamp = timestampSeconds;
                }

                var smoothedDetection = new DetectionResult
                {
                    Problem = detection.Problem,
                    Confidence = detection.Confidence,
                    ModelSource = detection.ModelSource,
                    ModelId = detection.ModelId,
                    ClassIndex = detection.ClassIndex,
                    Box = smoothed
                };

                matches.Add(new TrackMatch
                {
                    TrackId = track.TrackId,
                    Detection = smoothedDetection,
                    RawDetection = detection,
                    IsNewTrack = false
                });

                matchedTrackIdx.Add(ti);
                matchedDetIdx.Add(di);
            }

            // New tracks for unmatched detections
            for (int di = 0; di < detections.Count; di++)
            {
                if (matchedDetIdx.Contains(di)) continue;
                var det = detections[di];
                var newTrack = new TrackedDefect
                {
                    TrackId = _nextTrackId++,
                    DefectClass = det.Problem,
                    ModelSource = det.ModelSource,
                    ModelId = det.ModelId,
                    ClassIndex = det.ClassIndex,
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
                    IsLost = false
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

            // Age unmatched active tracks
            for (int ti = 0; ti < activeTracks.Count; ti++)
            {
                if (matchedTrackIdx.Contains(ti)) continue;
                activeTracks[ti].FramesSinceLastSeen++;
                if (activeTracks[ti].FramesSinceLastSeen > _maxFramesLost)
                    activeTracks[ti].IsLost = true;
            }

            return matches;
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
                ModelSource = t.ModelSource,
                ModelId = t.ModelId,
                ClassIndex = t.ClassIndex
            }).ToList();
        }

        public void Reset()
        {
            _allTracks.Clear();
            _nextTrackId = 1;
        }

        private static BoundingBox? PredictNextPosition(TrackedDefect track)
        {
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

        private static float Lerp(float prev, float curr, float alpha) =>
            prev + alpha * (curr - prev);

        private static float ComputeIoU(BoundingBox a, BoundingBox b)
        {
            float x1 = Math.Max(a.X, b.X), y1 = Math.Max(a.Y, b.Y);
            float x2 = Math.Min(a.X + a.Width, b.X + b.Width);
            float y2 = Math.Min(a.Y + a.Height, b.Y + b.Height);
            float iW = Math.Max(0, x2 - x1), iH = Math.Max(0, y2 - y1);
            float inter = iW * iH;
            float union = a.Width * a.Height + b.Width * b.Height - inter;
            return union <= 0 ? 0f : inter / union;
        }
    }

    // ── Supporting types ─────────────────────────────────────────

    public class TrackedDefect
    {
        public int TrackId { get; set; }
        public string DefectClass { get; set; } = string.Empty;
        public string ModelSource { get; set; } = string.Empty;

        /// <summary>Preserved from the detection for external mapping.</summary>
        public int ModelId { get; set; }
        /// <summary>Preserved from the detection for external mapping.</summary>
        public int ClassIndex { get; set; }

        public int FirstFrameNumber { get; set; }
        public double FirstTimestamp { get; set; }
        public int LastFrameNumber { get; set; }
        public double LastTimestamp { get; set; }
        public float LastConfidence { get; set; }

        public BoundingBox SmoothedBox { get; set; } = new();
        public BoundingBox RawBox { get; set; } = new();
        public BoundingBox? PredictedBox { get; set; }

        public float VelocityX { get; set; }
        public float VelocityY { get; set; }
        public float VelocityW { get; set; }
        public float VelocityH { get; set; }

        public BoundingBox BestBox { get; set; } = new();
        public float BestConfidence { get; set; }
        public int BestFrameNumber { get; set; }
        public double BestTimestamp { get; set; }

        public int TotalAppearances { get; set; }
        public int FramesSinceLastSeen { get; set; }
        public bool IsLost { get; set; }
    }

    public class TrackMatch
    {
        public int TrackId { get; set; }
        /// <summary>Smoothed detection — use for rendering.</summary>
        public DetectionResult Detection { get; set; } = new();
        /// <summary>Raw detection from model.</summary>
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
        /// <summary>Preserved for external mapping of video results.</summary>
        public int ModelId { get; set; }
        /// <summary>Preserved for external mapping of video results.</summary>
        public int ClassIndex { get; set; }
    }
}