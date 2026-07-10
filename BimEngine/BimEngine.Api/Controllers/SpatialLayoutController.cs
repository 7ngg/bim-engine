using BimEngine.SpatialLayout;
using Microsoft.AspNetCore.Mvc;

namespace BimEngine.Api.Controllers;

/// <summary>
/// The PLAN pipeline's HTTP entry point — fully separate from <see cref="ProjectsController"/>.
/// Accepts the relation-matrix request verbatim, generates the packed 3D boxes, hands them to the
/// spatial sink (logging demo, or file-drop for Revit), and returns the result so the caller sees
/// exactly what was produced.
/// </summary>
[ApiController]
[Route("spatial-layout")]
public sealed class SpatialLayoutController : ControllerBase
{
    private readonly SpatialLayoutEngine _engine;
    private readonly ISpatialLayoutSink _sink;
    private readonly ILogger<SpatialLayoutController> _logger;

    public SpatialLayoutController(
        SpatialLayoutEngine engine, ISpatialLayoutSink sink, ILogger<SpatialLayoutController> logger)
    {
        _engine = engine;
        _sink = sink;
        _logger = logger;
    }

    /// <summary>
    /// Accept a <see cref="SpatialLayoutRequest"/> (<c>spatial_units</c> + <c>relation_matrix</c>),
    /// pack it into 3D bounding boxes, publish to the spatial sink, and return 202 with the result.
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(SpatialLayoutResult), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Create([FromBody] SpatialLayoutRequest request, CancellationToken ct)
    {
        SpatialLayoutResult result;
        try
        {
            result = _engine.Generate(request);
        }
        catch (SpatialLayoutValidationException ex)
        {
            _logger.LogWarning("Rejected spatial layout: {Reason}", ex.Message);
            return Problem(detail: ex.Message, statusCode: StatusCodes.Status400BadRequest,
                title: "Spatial layout validation failed");
        }

        await _sink.SendAsync(result, ct);
        _logger.LogInformation("Published spatial layout {ProjectId} with {Count} unit(s)",
            result.ProjectId, result.Units.Count);

        return Accepted(value: result);
    }
}
