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
    private readonly IFloorPlanPromptService _plan;
    private readonly ILogger<ProjectsController> _logger;

    public ProjectsController(IRagService rag, IMessageQueue queue, IFloorPlanPromptService plan, ILogger<ProjectsController> logger)
    {
        _rag = rag;
        _queue = queue;
        _plan = plan;
        _logger = logger;
    }

    /// <summary>
    /// Accept building parameters, validate + enrich them into a GeometryCommand, publish it to
    /// the queue, and return 202 with the generated command so the caller sees what was produced.
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(GeometryCommand), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Create([FromBody] BuildingRequest request, CancellationToken ct)
    {
        GeometryCommand command;
        try
        {
            command = _rag.Enrich(request);
        }
        catch (RagValidationException ex)
        {
            _logger.LogWarning("Rejected request: {Reason}", ex.Message);
            return Problem(detail: ex.Message, statusCode: StatusCodes.Status400BadRequest, title: "Norm validation failed");
        }

        await _queue.PublishAsync(command, ct);
        _logger.LogInformation("Published project {ProjectId} with {RoomCount} rooms", command.ProjectId, command.Rooms.Count);

        // 202: work is queued; the consumer processes it asynchronously.
        return Accepted(value: command);
    }

    public sealed record UserPrompt(string Text);

    /// <summary>
    /// Accept a natural-language building brief, run the two-stage Gemini pipeline (extract params →
    /// generate layout variants), publish each variant to the queue, and return 202 with the commands.
    /// </summary>
    [HttpPost("prompt")]
    [ProducesResponseType(typeof(IReadOnlyList<GeometryCommand>), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> Prompt(UserPrompt prompt, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(prompt.Text))
            return Problem(detail: "Prompt text is required.", statusCode: StatusCodes.Status400BadRequest);

        IReadOnlyList<GeometryCommand> commands;
        try
        {
            commands = await _plan.GenerateVariantsAsync(prompt.Text, ct);
        }
        catch (FloorPlanConfigException ex)
        {
            _logger.LogError(ex, "Floor-plan service misconfigured");
            return Problem(detail: ex.Message, statusCode: StatusCodes.Status500InternalServerError, title: "Service not configured");
        }
        catch (FloorPlanUpstreamException ex)
        {
            _logger.LogWarning(ex, "Floor-plan generation failed upstream");
            return Problem(detail: ex.Message, statusCode: StatusCodes.Status502BadGateway, title: "Floor-plan generation failed");
        }

        foreach (var command in commands)
            await _queue.PublishAsync(command, ct);
        _logger.LogInformation("Published {Count} variant(s) from prompt", commands.Count);

        return Accepted(value: commands);
    }
}
