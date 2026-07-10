namespace BimEngine.SpatialLayout;

/// <summary>
/// Raised when a <see cref="SpatialLayoutRequest"/> is malformed (empty units, duplicate ids,
/// non-square matrix, out-of-range code, non-positive area/ratio/height). The API maps it to
/// HTTP 400 — mirroring <c>RagValidationException</c> in the other pipeline.
/// </summary>
public sealed class SpatialLayoutValidationException(string message) : Exception(message);
