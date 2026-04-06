using RoadDefectDetection.Configuration;
using RoadDefectDetection.DTOs;

namespace RoadDefectDetection.Services.Interfaces
{
    /// <summary>
    /// Maps internal detection results to external system class IDs and names.
    /// </summary>
    public interface IExternalMappingService
    {
        MappedDetectionResult MapDetection(DetectionResult detection);
        MappedDetectionResponse MapResponse(DetectionResponse response);
        List<ExternalClassMapping> GetAllMappings();
        List<ExternalClassMapping> GetMappingsForModel(int modelId);
        ExternalClassMapping? GetMapping(int modelId, int classIndex);
    }
}