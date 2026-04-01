using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using RoadDefectDetection.DTOs;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace RoadDefectDetection.Services
{
    /// <summary>
    /// Wraps a single YOLOv8 ONNX model. Includes enhanced NMS with:
    /// 1. Per-class NMS (standard)
    /// 2. Cross-class NMS (removes overlapping boxes of DIFFERENT classes)
    /// 3. Minimum box size filtering
    /// 4. Area-based duplicate removal
    /// </summary>
    public sealed class YoloDetector : IDisposable
    {
        private readonly InferenceSession _session;
        private readonly string[] _classNames;
        private readonly float _confidenceThreshold;
        private readonly float _iouThreshold;
        private readonly string _modelName;
        private readonly int _inputSize;
        private bool _disposed;

        /// <summary>
        /// Cross-class IoU threshold. If two detections of DIFFERENT classes
        /// overlap more than this, keep only the higher-confidence one.
        /// This prevents "Pothole" and "Alligator Crack" boxes on the same spot.
        /// </summary>
        private const float CrossClassIouThreshold = 0.60f;

        /// <summary>
        /// Minimum box dimension in pixels (original image space).
        /// Boxes smaller than this are likely noise.
        /// </summary>
        private const float MinBoxDimension = 8f;

        /// <summary>
        /// If one box contains more than this fraction of another box's area,
        /// they're considered duplicates (containment check).
        /// </summary>
        private const float ContainmentThreshold = 0.85f;

        public string ModelName => _modelName;
        public string[] ClassNames => _classNames;

        public YoloDetector(
            string onnxPath,
            string[] classNames,
            float confidenceThreshold,
            float iouThreshold,
            string modelName,
            int inputSize = 640)
        {
            if (!File.Exists(onnxPath))
                throw new FileNotFoundException(
                    $"ONNX model not found: {onnxPath}", onnxPath);

            _classNames = classNames ?? throw new ArgumentNullException(nameof(classNames));
            _confidenceThreshold = confidenceThreshold;
            _iouThreshold = iouThreshold;
            _modelName = modelName ?? throw new ArgumentNullException(nameof(modelName));
            _inputSize = inputSize;

            var opts = new Microsoft.ML.OnnxRuntime.SessionOptions
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
            };
            _session = new InferenceSession(onnxPath, opts);
        }

        public List<DetectionResult> Detect(byte[] imageBytes, float? confidenceOverride = null)
        {
            if (imageBytes == null || imageBytes.Length == 0)
                throw new ArgumentException("Image bytes cannot be null or empty.");

            float activeConf = confidenceOverride ?? _confidenceThreshold;

            int origW, origH;
            DenseTensor<float> inputTensor;

            using (var image = Image.Load<Rgb24>(imageBytes))
            {
                origW = image.Width;
                origH = image.Height;
                image.Mutate(ctx => ctx.Resize(_inputSize, _inputSize));

                inputTensor = new DenseTensor<float>(new[] { 1, 3, _inputSize, _inputSize });
                image.ProcessPixelRows(accessor =>
                {
                    for (int y = 0; y < _inputSize; y++)
                    {
                        Span<Rgb24> row = accessor.GetRowSpan(y);
                        for (int x = 0; x < _inputSize; x++)
                        {
                            Rgb24 px = row[x];
                            inputTensor[0, 0, y, x] = px.R / 255f;
                            inputTensor[0, 1, y, x] = px.G / 255f;
                            inputTensor[0, 2, y, x] = px.B / 255f;
                        }
                    }
                });
            }

            string inputName = _session.InputMetadata.Keys.First();
            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor(inputName, inputTensor)
            };

            using var results = _session.Run(inputs);
            Tensor<float> output = results.First().AsTensor<float>();

            int numClasses = _classNames.Length;
            int numDetections = output.Dimensions.Length == 3
                ? output.Dimensions[2] : 8400;

            float scaleX = (float)origW / _inputSize;
            float scaleY = (float)origH / _inputSize;

            var rawDetections = new List<DetectionResult>();

            for (int i = 0; i < numDetections; i++)
            {
                int bestClass = 0;
                float bestConf = output[0, 4, i];

                for (int c = 1; c < numClasses; c++)
                {
                    float conf = output[0, 4 + c, i];
                    if (conf > bestConf)
                    {
                        bestConf = conf;
                        bestClass = c;
                    }
                }

                if (bestConf < activeConf) continue;

                float cx = output[0, 0, i];
                float cy = output[0, 1, i];
                float w = output[0, 2, i];
                float h = output[0, 3, i];

                float x1 = (cx - w / 2f) * scaleX;
                float y1 = (cy - h / 2f) * scaleY;
                float boxW = w * scaleX;
                float boxH = h * scaleY;

                // Clamp to image boundaries
                x1 = Math.Max(0, x1);
                y1 = Math.Max(0, y1);
                boxW = Math.Min(boxW, origW - x1);
                boxH = Math.Min(boxH, origH - y1);

                // ── Filter: minimum box size ────────────────────
                if (boxW < MinBoxDimension || boxH < MinBoxDimension)
                    continue;

                rawDetections.Add(new DetectionResult
                {
                    Problem = _classNames[bestClass],
                    Confidence = bestConf,
                    ModelSource = _modelName,
                    Box = new BoundingBox
                    {
                        X = x1,
                        Y = y1,
                        Width = boxW,
                        Height = boxH
                    }
                });
            }

            // ── Enhanced NMS pipeline ───────────────────────────
            var afterPerClassNms = ApplyPerClassNms(rawDetections);
            var afterCrossClassNms = ApplyCrossClassNms(afterPerClassNms);
            var afterContainment = RemoveContainedBoxes(afterCrossClassNms);

            return afterContainment;
        }

        /// <summary>
        /// Standard per-class NMS: within each class, remove overlapping
        /// boxes keeping the highest confidence one.
        /// </summary>
        private List<DetectionResult> ApplyPerClassNms(
            List<DetectionResult> detections)
        {
            var results = new List<DetectionResult>();
            var grouped = detections.GroupBy(d => d.Problem);

            foreach (var group in grouped)
            {
                var sorted = group.OrderByDescending(d => d.Confidence).ToList();

                while (sorted.Count > 0)
                {
                    var best = sorted[0];
                    results.Add(best);
                    sorted.RemoveAt(0);
                    sorted.RemoveAll(d => ComputeIoU(best.Box, d.Box) > _iouThreshold);
                }
            }

            return results;
        }

        /// <summary>
        /// Cross-class NMS: if two detections of DIFFERENT classes overlap
        /// heavily (IoU > 0.60), keep only the higher-confidence one.
        /// 
        /// This handles cases like the same area being detected as both
        /// "Pothole" and "Alligator Crack".
        /// </summary>
        private List<DetectionResult> ApplyCrossClassNms(
            List<DetectionResult> detections)
        {
            if (detections.Count <= 1) return detections;

            var sorted = detections
                .OrderByDescending(d => d.Confidence)
                .ToList();
            var keep = new List<DetectionResult>();
            var suppressed = new HashSet<int>();

            for (int i = 0; i < sorted.Count; i++)
            {
                if (suppressed.Contains(i)) continue;

                keep.Add(sorted[i]);

                for (int j = i + 1; j < sorted.Count; j++)
                {
                    if (suppressed.Contains(j)) continue;

                    // Different class but high overlap → suppress lower confidence
                    float iou = ComputeIoU(sorted[i].Box, sorted[j].Box);
                    if (iou > CrossClassIouThreshold)
                    {
                        suppressed.Add(j);
                    }
                }
            }

            return keep;
        }

        /// <summary>
        /// Containment check: if box A is almost entirely inside box B
        /// (or vice versa), keep only the higher-confidence one.
        /// 
        /// This catches cases where a small box is inside a large box
        /// but their IoU is low (because the large box is much bigger).
        /// </summary>
        private List<DetectionResult> RemoveContainedBoxes(
            List<DetectionResult> detections)
        {
            if (detections.Count <= 1) return detections;

            var sorted = detections
                .OrderByDescending(d => d.Confidence)
                .ToList();
            var keep = new List<DetectionResult>();
            var suppressed = new HashSet<int>();

            for (int i = 0; i < sorted.Count; i++)
            {
                if (suppressed.Contains(i)) continue;
                keep.Add(sorted[i]);

                for (int j = i + 1; j < sorted.Count; j++)
                {
                    if (suppressed.Contains(j)) continue;

                    float containment = ComputeContainment(
                        sorted[i].Box, sorted[j].Box);

                    if (containment > ContainmentThreshold)
                    {
                        suppressed.Add(j);
                    }
                }
            }

            return keep;
        }

        /// <summary>
        /// Computes what fraction of the smaller box's area is contained
        /// within the larger box. Returns value between 0 and 1.
        /// </summary>
        private static float ComputeContainment(BoundingBox a, BoundingBox b)
        {
            float x1 = Math.Max(a.X, b.X);
            float y1 = Math.Max(a.Y, b.Y);
            float x2 = Math.Min(a.X + a.Width, b.X + b.Width);
            float y2 = Math.Min(a.Y + a.Height, b.Y + b.Height);

            float interW = Math.Max(0, x2 - x1);
            float interH = Math.Max(0, y2 - y1);
            float interArea = interW * interH;

            float areaA = a.Width * a.Height;
            float areaB = b.Width * b.Height;
            float smallerArea = Math.Min(areaA, areaB);

            return smallerArea <= 0 ? 0f : interArea / smallerArea;
        }

        private static float ComputeIoU(BoundingBox a, BoundingBox b)
        {
            float x1 = Math.Max(a.X, b.X);
            float y1 = Math.Max(a.Y, b.Y);
            float x2 = Math.Min(a.X + a.Width, b.X + b.Width);
            float y2 = Math.Min(a.Y + a.Height, b.Y + b.Height);

            float interW = Math.Max(0, x2 - x1);
            float interH = Math.Max(0, y2 - y1);
            float interArea = interW * interH;

            float areaA = a.Width * a.Height;
            float areaB = b.Width * b.Height;
            float unionArea = areaA + areaB - interArea;

            return unionArea <= 0 ? 0f : interArea / unionArea;
        }

        public void Dispose()
        {
            if (!_disposed) { _session?.Dispose(); _disposed = true; }
        }
    }
}