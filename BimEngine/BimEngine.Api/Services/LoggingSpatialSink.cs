using BimEngine.SpatialLayout;

namespace BimEngine.Api.Services;

/// <summary>
/// Zero-setup "mock Revit" for the spatial-layout pipeline (InMemory transport): logs the mass it
/// WOULD build for each unit. Proves the path client → API/engine → sink without any Revit or shared
/// folder — the counterpart to <c>MockRevitConsumer</c> in the other pipeline. Swapped for
/// <see cref="FileDropSpatialSink"/> under the <c>FileDrop</c> transport.
/// </summary>
public sealed class LoggingSpatialSink : ISpatialLayoutSink
{
    private readonly ILogger<LoggingSpatialSink> _logger;

    public LoggingSpatialSink(ILogger<LoggingSpatialSink> logger) => _logger = logger;

    public Task SendAsync(SpatialLayoutResult result, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "[MOCK REVIT/SPATIAL] Received {ProjectId}: {Count} unit(s)",
            result.ProjectId, result.Units.Count);

        foreach (var u in result.Units)
        {
            var min = u.Bbox.MinPoint;
            var max = u.Bbox.MaxPoint;
            _logger.LogInformation(
                "[MOCK REVIT/SPATIAL] Would create Mass '{Name}' (id {Id}) bbox min[{MinX}, {MinY}, {MinZ}] max[{MaxX}, {MaxY}, {MaxZ}]",
                u.Name, u.Id, min[0], min[1], min[2], max[0], max[1], max[2]);
        }

        return Task.CompletedTask;
    }
}
