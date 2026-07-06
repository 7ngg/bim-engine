using BimEngine.Api.Services;
using BimEngine.Core.Contracts;
using BimEngine.Core.Models;
using Microsoft.AspNetCore.Mvc;

namespace BimEngine.Api.Controllers;

[ApiController]
[Route("[controller]")]
public sealed class ProjectsController : ControllerBase
{
    private readonly IRagService _rag;
    private readonly IMessageQueue _queue;
    private readonly ILogger<ProjectsController> _logger;

    public ProjectsController(IRagService rag, IMessageQueue queue, ILogger<ProjectsController> logger)
    {
        _rag = rag;
        _queue = queue;
        _logger = logger;
    }

    /// <summary>
    /// Accept a rich, user-authored <see cref="FloorPlanBrief"/>, validate + expand it into a
    /// <see cref="GeometryCommand"/>, publish it to the queue, and return 202 with the generated
    /// command so the caller sees what was produced.
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(GeometryCommand), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Create([FromBody] FloorPlanBrief brief, CancellationToken ct)
    {
        GeometryCommand command;
        try
        {
            command = _rag.Enrich(brief);
        }
        catch (RagValidationException ex)
        {
            _logger.LogWarning("Rejected brief: {Reason}", ex.Message);
            return Problem(detail: ex.Message, statusCode: StatusCodes.Status400BadRequest, title: "Norm validation failed");
        }

        await _queue.PublishAsync(command, ct);
        _logger.LogInformation("Published project {ProjectId} with {RoomCount} rooms across {Floors} floor(s)",
            command.ProjectId, command.Rooms.Count, command.FloorCount);

        // 202: work is queued; the consumer processes it asynchronously.
        return Accepted(value: command);
    }
}
