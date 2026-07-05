using BimEngine.Core.Contracts;
using BimEngine.Core.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BimEngine.MockConsumer;

/// <summary>
/// Stand-in for the future Revit add-in. Subscribes to the queue and logs, per room, what it
/// WOULD build. This proves the end-to-end pipeline (client → API/RAG → queue → consumer)
/// without any Revit dependency.
///
/// REAL REVIT NOTE: this class cannot survive as-is inside Revit. The Revit API is single-threaded
/// and may only be touched on Revit's main thread. A production consumer would:
///   * live inside Revit's process, loaded as an add-in (.addin manifest),
///   * expose an IExternalCommand entry point (user clicks a ribbon button), and
///   * marshal each GeometryCommand onto the Revit thread via IExternalEventHandler +
///     ExternalEvent.Raise() before calling Document.Create.* APIs.
/// The <see cref="IGeometryConsumer"/> seam stays the same — only <see cref="ProcessAsync"/>'s body
/// changes from Console logging to real Revit element creation.
/// </summary>
public sealed class MockRevitConsumer : BackgroundService, IGeometryConsumer
{
    private readonly IMessageQueue _queue;
    private readonly ILogger<MockRevitConsumer> _logger;

    public MockRevitConsumer(IMessageQueue queue, ILogger<MockRevitConsumer> logger)
    {
        _queue = queue;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[MOCK REVIT] Consumer started. Waiting for geometry commands...");

        await foreach (var command in _queue.ConsumeAsync(stoppingToken))
        {
            try
            {
                await ProcessAsync(command, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[MOCK REVIT] Failed to process command for project {ProjectId}", command.ProjectId);
            }
        }
    }

    public Task ProcessAsync(GeometryCommand command, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "[MOCK REVIT] Received project {ProjectId}: {FloorCount} floor(s), {RoomCount} room(s)",
            command.ProjectId, command.FloorCount, command.Rooms.Count);

        foreach (var room in command.Rooms)
        {
            var adjacency = room.AdjacentTo.Count > 0
                ? string.Join(", ", room.AdjacentTo)
                : "(none)";

            _logger.LogInformation(
                "[MOCK REVIT] Would create Room '{Name}' ({Area} sqm) on Floor {Floor}, adjacent to: {Adjacent}",
                room.Name, room.AreaSqm, room.FloorIndex, adjacency);
        }

        return Task.CompletedTask;
    }
}
