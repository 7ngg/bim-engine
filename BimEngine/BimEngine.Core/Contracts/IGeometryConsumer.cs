using BimEngine.Core.Models;

namespace BimEngine.Core.Contracts;

/// <summary>
/// A sink that turns a <see cref="GeometryCommand"/> into actual building geometry.
///
/// SEAM: today the only implementation is the mock consumer that logs to the console.
/// The real Revit add-in will implement this same interface, but its body will run inside
/// Revit's process and call the Revit API (Document.Create, FilteredElementCollector, etc.).
/// Keeping this interface broker- and host-agnostic is what lets us swap the mock for Revit
/// without touching the API or the queue.
/// </summary>
public interface IGeometryConsumer
{
    Task ProcessAsync(GeometryCommand command, CancellationToken cancellationToken = default);
}
