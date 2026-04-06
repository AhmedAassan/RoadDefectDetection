using Microsoft.Extensions.Options;
using RoadDefectDetection.Configuration;
using RoadDefectDetection.DTOs;
using RoadDefectDetection.Services.Interfaces;

namespace RoadDefectDetection.Services
{
    /// <summary>
    /// Configuration-driven service that maps internal model detections
    /// to external system class IDs. Thread-safe: the lookup dictionary
    /// is built once at construction and never modified.
    /// </summary>
    public sealed class ExternalMappingService : IExternalMappingService
    {
        private readonly List<ExternalClassMapping> _allMappings;
        private readonly Dictionary<(int ModelId, int ClassIndex), ExternalClassMapping> _lookup;
        private readonly ILogger<ExternalMappingService> _logger;

        public ExternalMappingService(
            IConfiguration configuration,
            ILogger<ExternalMappingService> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _allMappings = new List<ExternalClassMapping>();
            configuration.GetSection("ExternalClassMappings").Bind(_allMappings);

            _lookup = new Dictionary<(int, int), ExternalClassMapping>();

            foreach (var m in _allMappings)
            {
                var key = (m.ModelId, m.InternalModelClassIndex);
                if (_lookup.ContainsKey(key))
                {
                    _logger.LogWarning(
                        "Duplicate mapping for ModelId={ModelId}, ClassIndex={ClassIndex}. " +
                        "Keeping ExternalClassId={Existing}, ignoring ExternalClassId={Dup}.",
                        m.ModelId, m.InternalModelClassIndex,
                        _lookup[key].ExternalClassId, m.ExternalClassId);
                    continue;
                }
                _lookup[key] = m;
            }

            _logger.LogInformation(
                "External mapping service: {Total} mapping(s), {Models} model(s), {Lookup} lookup entries.",
                _allMappings.Count,
                _allMappings.Select(m => m.ModelId).Distinct().Count(),
                _lookup.Count);
        }

        public MappedDetectionResult MapDetection(DetectionResult detection)
        {
            ArgumentNullException.ThrowIfNull(detection);

            var mapped = new MappedDetectionResult
            {
                InternalClassName = detection.Problem,
                InternalClassIndex = detection.ClassIndex,
                ModelId = detection.ModelId,
                Confidence = detection.Confidence,
                ModelSource = detection.ModelSource,
                Box = detection.Box
            };

            if (_lookup.TryGetValue((detection.ModelId, detection.ClassIndex), out var ext))
            {
                mapped.ExternalClassId = ext.ExternalClassId;
                mapped.ExternalClassName = ext.ExternalClassName;
                mapped.Severity = ext.Severity;
                mapped.Category = ext.Category;
            }
            else
            {
                _logger.LogDebug(
                    "No external mapping for ModelId={ModelId}, ClassIndex={ClassIndex} ('{Name}').",
                    detection.ModelId, detection.ClassIndex, detection.Problem);
            }

            return mapped;
        }

        public MappedDetectionResponse MapResponse(DetectionResponse response)
        {
            ArgumentNullException.ThrowIfNull(response);

            var dets = response.Detections.Select(MapDetection).ToList();
            int mappedCount = dets.Count(d => d.HasExternalMapping);
            int unmapped = dets.Count(d => !d.HasExternalMapping);

            if (unmapped > 0)
                _logger.LogWarning(
                    "'{Name}': {Unmapped}/{Total} detection(s) have no external mapping.",
                    response.ImageName, unmapped, dets.Count);

            return new MappedDetectionResponse
            {
                Success = response.Success,
                Message = response.Message,
                ImageName = response.ImageName,
                TotalProblemsFound = response.TotalProblemsFound,
                MappedDetections = mappedCount,
                UnmappedDetections = unmapped,
                ProcessingTimeMs = response.ProcessingTimeMs,
                Detections = dets
            };
        }

        public List<ExternalClassMapping> GetAllMappings()
            => _allMappings.ToList();

        public List<ExternalClassMapping> GetMappingsForModel(int modelId)
            => _allMappings.Where(m => m.ModelId == modelId).ToList();

        public ExternalClassMapping? GetMapping(int modelId, int classIndex)
        {
            _lookup.TryGetValue((modelId, classIndex), out var m);
            return m;
        }
    }
}