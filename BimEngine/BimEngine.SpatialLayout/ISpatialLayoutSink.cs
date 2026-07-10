namespace BimEngine.SpatialLayout;

/// <summary>
/// Where a generated <see cref="SpatialLayoutResult"/> is handed off. Deliberately its own seam
/// (parallel to, and independent of, the other pipeline's <c>IMessageQueue</c>) so the PLAN model
/// travels untouched. Implementations: a logging sink for the zero-setup demo, and a file-drop sink
/// that writes JSON the Revit mass consumer picks up.
/// </summary>
public interface ISpatialLayoutSink
{
    Task SendAsync(SpatialLayoutResult result, CancellationToken cancellationToken = default);
}
