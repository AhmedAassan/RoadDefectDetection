using RoadDefectDetection.Configuration;
using RoadDefectDetection.DTOs;

namespace RoadDefectDetection.Services.Interfaces
{
    /// <summary>
    /// Maps internal detection results to external system class IDs and names.
    /// 
    /// The external system (UI) has its own numbering scheme for defect types
    /// that may not match the ONNX model's class indices. This service bridges
    /// the gap using configuration-driven mappings.
    /// 
    /// Mapping key: (ModelId, InternalClassIndex) → ExternalClassMapping
    /// 
    /// Example flow:
    ///   1. Model 1 detects class index 2 with confidence 0.85
    ///   2. Internally, index 2 = "Pothole" (from ModelConfig.Classes)
    ///   3. Mapper looks up (ModelId=1, ClassIndex=2)
    ///   4. Finds ExternalClassId=1, ExternalClassName="Potholes"
    ///   5. Returns MappedDetectionResult with both internal and external data
    /// </summary>
    public interface IExternalMappingService
    {
        /// <summary>
        /// Maps a single detection result to the external system format.
        /// </summary>
        /// <param name="detection">The internal detection result from a model.</param>
        /// <returns>A mapped result containing both internal and external identifiers.</returns>
        MappedDetectionResult MapDetection(DetectionResult detection);

        /// <summary>
        /// Maps a complete detection response to the external system format.
        /// All detections in the response are individually mapped.
        /// </summary>
        /// <param name="response">The internal detection response.</param>
        /// <returns>A mapped response ready for the external system.</returns>
        MappedDetectionResponse MapResponse(DetectionResponse response);

        /// <summary>
        /// Returns all configured external class mappings.
        /// Useful for admin endpoints and debugging.
        /// </summary>
        /// <returns>List of all mapping configurations.</returns>
        List<ExternalClassMapping> GetAllMappings();

        /// <summary>
        /// Returns mappings filtered by model ID.
        /// </summary>
        /// <param name="modelId">The model ID to filter by.</param>
        /// <returns>List of mappings for the specified model.</returns>
        List<ExternalClassMapping> GetMappingsForModel(int modelId);

        /// <summary>
        /// Looks up the external mapping for a specific model + class index combination.
        /// </summary>
        /// <param name="modelId">The model ID.</param>
        /// <param name="classIndex">The class index within that model.</param>
        /// <returns>The mapping if found; null otherwise.</returns>
        ExternalClassMapping? GetMapping(int modelId, int classIndex);
    }
}