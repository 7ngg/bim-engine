using Autodesk.Revit.UI;
using BimEngine.Core.Contracts;
using BimEngine.Core.Models;

namespace BimEngine.RevitAddin;

/// <summary>
/// Real implementation of the <see cref="IGeometryConsumer"/> seam from BimEngine.Core (the mock
/// logged; this drives Revit). It deliberately does NOT build geometry directly: it is called on a
/// background thread, so it only enqueues the command and raises the ExternalEvent that marshals
/// the work onto Revit's main API thread (see <see cref="GeometryEventHandler"/>).
/// </summary>
public sealed class RevitGeometryConsumer : IGeometryConsumer
{
    private readonly GeometryEventHandler _handler;
    private readonly ExternalEvent _externalEvent;

    public RevitGeometryConsumer(GeometryEventHandler handler, ExternalEvent externalEvent)
    {
        _handler = handler;
        _externalEvent = externalEvent;
    }

    public Task ProcessAsync(GeometryCommand command, CancellationToken cancellationToken = default)
    {
        _handler.Enqueue(command);
        _externalEvent.Raise();
        return Task.CompletedTask;
    }
}
