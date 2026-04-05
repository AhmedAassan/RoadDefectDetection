using RoadDefectDetection.Configuration;
using RoadDefectDetection.DTOs;
using RoadDefectDetection.Services.Interfaces;

namespace RoadDefectDetection.Services
{
    /// <summary>
    /// Configuration-driven mapping service that translates internal model
    /// detections to external system class IDs.
    /// 
    /// Mappings are loaded once at startup from the "ExternalClassMappings"
    /// section of appsettings.json and stored in a dictionary keyed by
    /// (ModelId, InternalClassIndex) for O(1) lookups.
    /// 
    /// To add or modify mappings, edit appsettings.json — no code changes needed.
    /// To add mappings for a new model, just add new entries with the new ModelId.
    /// 
    /// Thread-safe: the mapping dictionary is built once at construction
    /// and never modified afterward (effectively immutable).
    /// </summary>
    public sealed class ExternalMappingService : IExternalMappingService
    {
        /// <summary>
        /// All mappings loaded from configuration.
        /// </summary>
        private readonly List<ExternalClassMapping> _allMappings;

        /// <summary>
        /// Fast lookup dictionary. Key = (ModelId, InternalClassIndex).
        /// Built once at construction time.
        /// </summary>
        private readonly Dictionary<(int ModelId, int ClassIndex), ExternalClassMapping> _mappingLookup;

        private readonly ILogger<ExternalMappingService> _logger;

        public ExternalMappingService(
            IConfiguration configuration,
            ILogger<ExternalMappingService> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            // ── Load mappings from configuration ────────────────
            _allMappings = new List<ExternalClassMapping>();
            configuration.GetSection("ExternalClassMappings").Bind(_allMappings);

            // ── Build fast lookup dictionary ────────────────────
            _mappingLookup = new Dictionary<(int, int), ExternalClassMapping>();

            foreach (var mapping in _allMappings)
            {
                var key = (mapping.ModelId, mapping.InternalModelClassIndex);

                if (_mappingLookup.ContainsKey(key))
                {
                    _logger.LogWarning(
                        "Duplicate external mapping for ModelId={ModelId}, ClassIndex={ClassIndex}. " +
                        "Keeping first occurrence (ExternalClassId={ExistingId}), " +
                        "ignoring duplicate (ExternalClassId={DuplicateId}).",
                        mapping.ModelId,
                        mapping.InternalModelClassIndex,
                        _mappingLookup[key].ExternalClassId,
                        mapping.ExternalClassId);
                    continue;
                }

                _mappingLookup[key] = mapping;
            }

            _logger.LogInformation(
                "External mapping service initialized with {Total} mapping(s) " +
                "across {Models} model(s). Lookup entries: {Lookup}.",
                _allMappings.Count,
                _allMappings.Select(m => m.ModelId).Distinct().Count(),
                _mappingLookup.Count);

            // Log each mapping for visibility
            foreach (var mapping in _allMappings)
            {
                _logger.LogDebug(
                    "  Mapping: Model {ModelId} ClassIdx {ClassIdx} → " +
                    "ExternalId {ExtId} '{ExtName}' [{Severity}/{Category}]",
                    mapping.ModelId,
                    mapping.InternalModelClassIndex,
                    mapping.ExternalClassId,
                    mapping.ExternalClassName,
                    mapping.Severity,
                    mapping.Category);
            }
        }

        /// <inheritdoc />
        public MappedDetectionResult MapDetection(DetectionResult detection)
        {
            if (detection == null)
                throw new ArgumentNullException(nameof(detection));

            var mapped = new MappedDetectionResult
            {
                // ── Copy internal data ──────────────────────────
                InternalClassName = detection.Problem,
                InternalClassIndex = detection.ClassIndex,
                ModelId = detection.ModelId,
                Confidence = detection.Confidence,
                ModelSource = detection.ModelSource,
                Box = detection.Box
            };

            // ── Look up external mapping ────────────────────────
            var key = (detection.ModelId, detection.ClassIndex);

            if (_mappingLookup.TryGetValue(key, out var externalMapping))
            {
                mapped.ExternalClassId = externalMapping.ExternalClassId;
                mapped.ExternalClassName = externalMapping.ExternalClassName;
                mapped.Severity = externalMapping.Severity;
                mapped.Category = externalMapping.Category;
            }
            else
            {
                _logger.LogDebug(
                    "No external mapping found for ModelId={ModelId}, " +
                    "ClassIndex={ClassIndex} ('{ClassName}'). " +
                    "Detection will be returned without external mapping.",
                    detection.ModelId,
                    detection.ClassIndex,
                    detection.Problem);
            }

            return mapped;
        }

        /// <inheritdoc />
        public MappedDetectionResponse MapResponse(DetectionResponse response)
        {
            if (response == null)
                throw new ArgumentNullException(nameof(response));

            var mappedDetections = response.Detections
                .Select(MapDetection)
                .ToList();

            int mappedCount = mappedDetections.Count(d => d.HasExternalMapping);
            int unmappedCount = mappedDetections.Count(d => !d.HasExternalMapping);

            if (unmappedCount > 0)
            {
                _logger.LogWarning(
                    "Image '{ImageName}': {Unmapped} of {Total} detection(s) " +
                    "have no external mapping.",
                    response.ImageName,
                    unmappedCount,
                    mappedDetections.Count);
            }

            return new MappedDetectionResponse
            {
                Success = response.Success,
                Message = response.Message,
                ImageName = response.ImageName,
                TotalProblemsFound = response.TotalProblemsFound,
                MappedDetections = mappedCount,
                UnmappedDetections = unmappedCount,
                ProcessingTimeMs = response.ProcessingTimeMs,
                Detections = mappedDetections
            };
        }

        /// <inheritdoc />
        public List<ExternalClassMapping> GetAllMappings()
        {
            return _allMappings.ToList(); // Return copy
        }

        /// <inheritdoc />
        public List<ExternalClassMapping> GetMappingsForModel(int modelId)
        {
            return _allMappings
                .Where(m => m.ModelId == modelId)
                .ToList();
        }

        /// <inheritdoc />
        public ExternalClassMapping? GetMapping(int modelId, int classIndex)
        {
            _mappingLookup.TryGetValue((modelId, classIndex), out var mapping);
            return mapping;
        }
    }
}