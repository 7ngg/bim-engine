using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BimEngine.Core.Models;
using NJsonSchema;

namespace BimEngine.Api.Services;

/// <summary>
/// Two-stage Gemini Interactions pipeline:
///   1. NL prompt        → <see cref="FloorPlanExtraction"/>  (structured params)
///   2. FloorPlanExtraction → <see cref="LayoutVariants"/>    (N buildable layouts, one call)
/// Each variant is mapped to a <see cref="GeometryCommand"/> with a server-assigned ProjectId and
/// the full stage-1 brief attached so nothing useful to Revit is dropped.
/// </summary>
public sealed class FloorPlanPromptService : IFloorPlanPromptService
{
    private const string InteractionsUrl = "https://generativelanguage.googleapis.com/v1beta/interactions";
    private const string Model = "gemini-3.5-flash";
    private const int VariantCount = 3;

    // Case-insensitive so PascalCase model output (per the emitted JSON Schema) round-trips cleanly.
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<FloorPlanPromptService> _logger;

    public FloorPlanPromptService(IHttpClientFactory httpClientFactory, IConfiguration config,
        ILogger<FloorPlanPromptService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _config = config;
        _logger = logger;
    }

    public async Task<IReadOnlyList<GeometryCommand>> GenerateVariantsAsync(string prompt, CancellationToken ct)
    {
        var apiKey = _config["GEMINI_API_KEY"];
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new FloorPlanConfigException("GEMINI_API_KEY is not configured.");

        var http = _httpClientFactory.CreateClient();

        // --- Stage 1: extract structured params from the prose ------------------------------------
        var extractInput = string.Format(ExtractPromptTemplate, prompt);
        var extraction = await CallGeminiAsync<FloorPlanExtraction>(
            http, apiKey, extractInput, typeof(FloorPlanExtraction), ct);
        _logger.LogInformation("Extracted brief: {Rooms} room spec(s), confidence {Confidence}",
            extraction.Rooms.Count, extraction.ExtractionMeta?.Confidence ?? "unknown");

        // --- Stage 2: generate N buildable layouts from the params (single call) ------------------
        var layoutInput = string.Format(LayoutPromptTemplate,
            VariantCount, JsonSerializer.Serialize(extraction, JsonOpts));
        var layouts = await CallGeminiAsync<LayoutVariants>(
            http, apiKey, layoutInput, typeof(LayoutVariants), ct);

        if (layouts.Variants.Count == 0)
            throw new FloorPlanUpstreamException("Gemini returned no floor-plan variants.");

        // --- Map each variant → GeometryCommand, injecting the shared brief -----------------------
        var commands = new List<GeometryCommand>(layouts.Variants.Count);
        for (var i = 0; i < layouts.Variants.Count; i++)
        {
            var v = layouts.Variants[i];
            var projectId = $"PRJ-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}-v{i + 1}";
            commands.Add(new GeometryCommand(
                projectId,
                v.FloorCount > 0 ? v.FloorCount : 1,
                v.Rooms,
                v.FloorHeightM > 0 ? v.FloorHeightM : 3.0,
                v.Doors,
                Brief: extraction));
        }

        return commands;
    }

    // --- Gemini Interactions call + structured-output plumbing ------------------------------------
    private async Task<T> CallGeminiAsync<T>(HttpClient http, string apiKey, string input,
        Type schemaType, CancellationToken ct)
    {
        // NJsonSchema emits a JSON Schema string; parse it into a node so it nests as an object
        // (not a string) under response_format.schema.
        var schemaNode = JsonNode.Parse(JsonSchema.FromType(schemaType).ToJson());

        var payload = JsonSerializer.Serialize(new
        {
            model = Model,
            input,
            response_format = new
            {
                type = "text",
                mime_type = "application/json",
                schema = schemaNode,
            },
        });

        var body = await SendWithRetryAsync(http, apiKey, payload, ct);
        var json = ExtractJson(body);
        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonOpts)
                ?? throw new FloorPlanUpstreamException("Gemini returned a null plan.");
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse Gemini output as {Type}: {Json}", typeof(T).Name, json);
            throw new FloorPlanUpstreamException($"Gemini returned unparseable JSON for {typeof(T).Name}.");
        }
    }

    // Posts the payload, retrying transient upstream 5xx (Gemini asks callers to "try again later").
    // Returns the success body; throws FloorPlanUpstreamException on a non-transient failure, an
    // exhausted retry budget, or an HTTP timeout (distinguished from real caller cancellation).
    private const int MaxAttempts = 3;
    private async Task<string> SendWithRetryAsync(HttpClient http, string apiKey, string payload, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, InteractionsUrl)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            };
            request.Headers.Add("x-goog-api-key", apiKey);

            HttpResponseMessage response;
            try
            {
                response = await http.SendAsync(request, ct);
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(ex, "Gemini request errored (attempt {Attempt}/{Max})", attempt, MaxAttempts);
                if (attempt >= MaxAttempts) throw new FloorPlanUpstreamException("Gemini is unreachable.");
                await DelayBeforeRetryAsync(attempt, ct);
                continue;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // The HttpClient timeout (not the caller) elapsed — treat as an upstream failure.
                _logger.LogWarning("Gemini request timed out (attempt {Attempt}/{Max})", attempt, MaxAttempts);
                if (attempt >= MaxAttempts) throw new FloorPlanUpstreamException("Gemini timed out.");
                await DelayBeforeRetryAsync(attempt, ct);
                continue;
            }

            using (response)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                if (response.IsSuccessStatusCode)
                    return body;

                // Log the upstream detail; never surface it (or the key) verbatim to the caller.
                _logger.LogWarning("Gemini returned {Status} (attempt {Attempt}/{Max}): {Body}",
                    (int)response.StatusCode, attempt, MaxAttempts, body);

                var transient = (int)response.StatusCode >= 500
                    || response.StatusCode == System.Net.HttpStatusCode.TooManyRequests; // 429 rate-limit
                if (!transient || attempt >= MaxAttempts)
                    throw new FloorPlanUpstreamException($"Gemini request failed ({(int)response.StatusCode}).");
                await DelayBeforeRetryAsync(attempt, ct);
            }
        }
    }

    private static Task DelayBeforeRetryAsync(int attempt, CancellationToken ct) =>
        Task.Delay(TimeSpan.FromMilliseconds(500 * attempt), ct); // simple linear backoff

    // Pulls the generated JSON text out of the Interactions envelope: steps[].content[].text.
    private static string ExtractJson(string responseBody)
    {
        using var doc = JsonDocument.Parse(responseBody);
        if (doc.RootElement.TryGetProperty("steps", out var steps) &&
            steps.ValueKind == JsonValueKind.Array)
        {
            foreach (var step in steps.EnumerateArray())
            {
                if (!step.TryGetProperty("content", out var content) ||
                    content.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var item in content.EnumerateArray())
                {
                    if (item.TryGetProperty("text", out var text) &&
                        text.ValueKind == JsonValueKind.String)
                    {
                        var value = text.GetString();
                        if (!string.IsNullOrWhiteSpace(value))
                            return value;
                    }
                }
            }
        }

        throw new FloorPlanUpstreamException("Gemini response contained no text output to parse.");
    }

    private const string ExtractPromptTemplate = """
        Extract floor plan parameters from this user prompt into the required JSON schema.

        Rules:
        - If information is missing, make a reasonable assumption and record it in ExtractionMeta.Assumptions.
        - If something critical is missing (plot shape, orientation) that blocks generation, add it to ExtractionMeta.MissingCriticalInfo.
        - Never invent room counts wildly inconsistent with the stated total area (rough rule: a livable room is >= 6 sqm each).

        User prompt: "{0}"
        """;

    private const string LayoutPromptTemplate = """
        You are a floor-plan layout engine. From the extracted brief below, produce {0} DISTINCT,
        buildable layout variants that satisfy it.

        Requirements for every variant:
        - Every room needs a rectangular Footprint in metres (OriginXm, OriginYm, WidthM, DepthM),
          floor-local (origin per FloorIndex), non-overlapping within a floor.
        - Give each room an AreaSqm consistent with its footprint (WidthM * DepthM).
        - Fill Type, PrivacyLevel, RequiresNaturalLight, RequiresEnsuite, IsPrimary from the brief.
        - Populate AdjacentTo, and add a Door on each shared wall between adjacent rooms on the same floor.
        - Vary the variants meaningfully (e.g. open-plan vs compartmentalised); set a short Label on each.

        Extracted brief (JSON):
        {1}
        """;
}
