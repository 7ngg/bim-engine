using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using BimEngine.Core.Models;
using NJsonSchema;
using NJsonSchema.Generation;

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

    // Case-insensitive so PascalCase model output (per the emitted JSON Schema) round-trips cleanly;
    // string-enum converter so RoomType round-trips by name.
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    // Generate the schema from the same System.Text.Json contract we (de)serialize with, so property
    // names and the RoomType string-enum match what Gemini is asked to produce.
    private static readonly JsonSchemaGeneratorSettings SchemaSettings =
        new SystemTextJsonSchemaGeneratorSettings { SerializerOptions = JsonOpts };

    // Reject-and-regenerate ceiling on absurd string lengths (a repetition loop that still yields
    // valid JSON — parse alone will not catch it).
    private const int MaxStringLen = 80;

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
        var (http, apiKey) = CreateClient();

        // --- Stage 1: extract structured params from the prose ------------------------------------
        var extractInput = string.Format(ExtractPromptTemplate, prompt);
        var extraction = await CallGeminiAsync<FloorPlanExtraction>(
            http, apiKey, extractInput, typeof(FloorPlanExtraction), ct, ValidateExtraction);
        _logger.LogInformation("Extracted brief: {Rooms} room spec(s), confidence {Confidence}",
            extraction.Rooms.Count, extraction.ExtractionMeta?.Confidence ?? "unknown");

        // --- Stage 2: generate N buildable layouts from the params (single call) ------------------
        var variantCount = _config.GetValue("Gemini:VariantCount", 3);
        var layoutInput = string.Format(LayoutPromptTemplate,
            variantCount, JsonSerializer.Serialize(extraction, JsonOpts));
        var layouts = await CallGeminiAsync<LayoutVariants>(
            http, apiKey, layoutInput, typeof(LayoutVariants), ct, ValidateLayouts);

        return MapToCommands(layouts, brief: extraction);
    }

    public async Task<IReadOnlyList<GeometryCommand>> GenerateVariantsDirectAsync(string prompt, CancellationToken ct)
    {
        var (http, apiKey) = CreateClient();

        // Single call: NL prompt → layout variants, no separate extraction step.
        var variantCount = _config.GetValue("Gemini:VariantCount", 3);
        var input = string.Format(DirectPromptTemplate, variantCount, prompt);
        var layouts = await CallGeminiAsync<LayoutVariants>(
            http, apiKey, input, typeof(LayoutVariants), ct, ValidateLayouts);

        return MapToCommands(layouts, brief: null);
    }

    // Creates the Gemini HTTP client with a generous timeout, validating the API key up front.
    private (HttpClient Http, string ApiKey) CreateClient()
    {
        var apiKey = _config["GEMINI_API_KEY"];
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new FloorPlanConfigException("GEMINI_API_KEY is not configured.");

        var http = _httpClientFactory.CreateClient();
        // Structured layout generation routinely exceeds the 100s default; give it headroom
        // (per-attempt) so a slow-but-valid response is not killed mid-flight.
        http.Timeout = TimeSpan.FromSeconds(_config.GetValue("Gemini:TimeoutSeconds", 180));
        return (http, apiKey);
    }

    // Maps generated variants → publishable commands, assigning ProjectIds and attaching the brief
    // (null on the one-stage path).
    private static IReadOnlyList<GeometryCommand> MapToCommands(LayoutVariants layouts, FloorPlanExtraction? brief)
    {
        if (layouts.Variants.Count == 0)
            throw new FloorPlanUpstreamException("Gemini returned no floor-plan variants.");

        var commands = new List<GeometryCommand>(layouts.Variants.Count);
        for (var i = 0; i < layouts.Variants.Count; i++)
        {
            var v = layouts.Variants[i];
            var projectId = $"PRJ-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}-v{i + 1}";

            // Footprint is the source of truth for size: backfill AreaSqm from it when the model left
            // it at 0, so downstream consumers get a consistent area.
            var rooms = v.Rooms
                .Select(r => r.AreaSqm > 0 || r.Footprint is null
                    ? r
                    : r with { AreaSqm = Math.Round(r.Footprint.WidthM * r.Footprint.DepthM, 2) })
                .ToList();

            commands.Add(new GeometryCommand(
                projectId,
                v.FloorCount > 0 ? v.FloorCount : 1,
                rooms,
                v.FloorHeightM > 0 ? v.FloorHeightM : 3.0,
                v.Doors,
                Brief: brief));
        }

        return commands;
    }

    // --- Gemini Interactions call + structured-output plumbing ------------------------------------
    private async Task<T> CallGeminiAsync<T>(HttpClient http, string apiKey, string input,
        Type schemaType, CancellationToken ct, Func<T, string?>? validate = null)
    {
        var schemaJson = JsonSchema.FromType(schemaType, SchemaSettings).ToJson();

        // DIAGNOSTIC: Gemini's response_format.schema is an OpenAPI subset and does NOT resolve
        // $ref/definitions. NJsonSchema emits exactly those for nested records, which can make the
        // model stall. Log the shape so we can see whether the emitted schema contains them.
        _logger.LogInformation(
            "Schema for {Type}: {Len} chars, hasRef={HasRef}, hasDefs={HasDefs}",
            schemaType.Name, schemaJson.Length,
            schemaJson.Contains("$ref"), schemaJson.Contains("\"definitions\""));

        // generation_config: cap output + keep thinking short so a large structured request does
        // not run away (unbounded output on a big schema looks like an infinite stall).
        var generationConfig = new JsonObject
        {
            ["max_output_tokens"] = _config.GetValue("Gemini:MaxOutputTokens", 16384),
            ["thinking_level"] = _config["Gemini:ThinkingLevel"] ?? "low",
            // Non-zero sampling breaks the greedy-decoding repetition loops that otherwise fill a
            // free-form string field (e.g. Type) with a runaway token until the output truncates.
            ["temperature"] = _config.GetValue("Gemini:Temperature", 0.7),
            ["top_p"] = _config.GetValue("Gemini:TopP", 0.95),
        };

        var request = new JsonObject
        {
            ["model"] = Model,
            ["generation_config"] = generationConfig,
        };

        // PROBE: set Gemini:UseResponseFormat=false to drop native structured output and instead
        // ask for the schema in the prompt text. If stage 2 then returns, the response_format schema
        // (its $ref/definitions) is the stall.
        if (_config.GetValue("Gemini:UseResponseFormat", true))
        {
            request["input"] = input;
            request["response_format"] = new JsonObject
            {
                ["type"] = "text",
                ["mime_type"] = "application/json",
                // Gemini's schema is an OpenAPI subset that does NOT resolve $ref. Inline all
                // definitions + strip unsupported keywords, else the model stalls.
                ["schema"] = InlineSchema(JsonNode.Parse(schemaJson)!),
            };
        }
        else
        {
            request["input"] = input
                + "\n\nReturn ONLY valid JSON, no markdown, matching this JSON Schema:\n" + schemaJson;
        }

        var payload = request.ToJsonString();

        // gemini-3.5-flash structured output occasionally degenerates into a token-repetition loop
        // (a runaway digit/char run in one field). That either truncates the JSON (parse fails) OR
        // yields valid-but-garbage JSON (a giant string). Both are stochastic, so on either a parse
        // failure or a semantic-validation failure we re-generate rather than fail/publish garbage.
        for (var attempt = 1; ; attempt++)
        {
            var sw = Stopwatch.StartNew();
            var body = await SendWithRetryAsync(http, apiKey, payload, ct);
            _logger.LogInformation("Gemini {Type} call returned in {Ms} ms (attempt {Attempt}/{Max})",
                schemaType.Name, sw.ElapsedMilliseconds, attempt, MaxParseAttempts);

            var json = StripFences(ExtractJson(body));
            try
            {
                var result = JsonSerializer.Deserialize<T>(json, JsonOpts)
                    ?? throw new FloorPlanUpstreamException("Gemini returned a null plan.");

                // Semantic gate: valid JSON is not enough — reject degenerate/incomplete content so
                // it triggers a regeneration instead of being published downstream.
                if (validate?.Invoke(result) is { } error)
                    throw new RegenerateException(error);

                return result;
            }
            catch (Exception ex) when (ex is JsonException or RegenerateException)
            {
                var head = json.Length > 400 ? json[..400] + "…" : json;
                _logger.LogWarning("Rejected Gemini output as {Type} (attempt {Attempt}/{Max}): {Reason}. Head: {Head}",
                    typeof(T).Name, attempt, MaxParseAttempts, ex.Message, head);
                if (attempt >= MaxParseAttempts)
                    throw new FloorPlanUpstreamException($"Gemini returned unusable output for {typeof(T).Name}: {ex.Message}");
            }
        }
    }

    private const int MaxParseAttempts = 3;

    // Signals a semantically-invalid generation that should be retried (not surfaced as-is).
    private sealed class RegenerateException(string message) : Exception(message);

    // --- Semantic validators (return an error message to reject-and-retry, or null if acceptable) --
    private static string? ValidateLayouts(LayoutVariants layouts)
    {
        if (layouts.Variants.Count == 0) return "no variants";
        foreach (var v in layouts.Variants)
        {
            if (v.Label is { Length: > MaxStringLen }) return "variant label too long (loop?)";
            if (v.Rooms.Count == 0) return "a variant has no rooms";
            foreach (var r in v.Rooms)
            {
                if (string.IsNullOrWhiteSpace(r.Name) || r.Name.Length > MaxStringLen)
                    return $"bad room name (loop?): '{Trunc(r.Name)}'";
                // AreaSqm is derived from the footprint downstream, so the footprint is the source of
                // truth for size — the model often leaves AreaSqm at 0 and that is fine.
                if (r.Footprint is null) return $"room '{r.Name}' has no footprint";
                if (r.Footprint.WidthM <= 0 || r.Footprint.DepthM <= 0)
                    return $"room '{r.Name}' has a non-positive footprint";
                if (r.AdjacentTo is null) return $"room '{r.Name}' has null AdjacentTo";
            }
        }
        return null;
    }

    private static string? ValidateExtraction(FloorPlanExtraction extraction)
    {
        foreach (var r in extraction.Rooms)
        {
            if (string.IsNullOrWhiteSpace(r.Id) || r.Id.Length > MaxStringLen)
                return $"bad room id (loop?): '{Trunc(r.Id)}'";
            if (r.Count is < 0 or > 100) return $"implausible room count {r.Count} for '{r.Id}'";
        }
        return null;
    }

    private static string Trunc(string? s) =>
        s is null ? "" : s.Length > 40 ? s[..40] + "…" : s;

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

    // Turns an NJsonSchema document (root + "definitions" + "$ref") into a self-contained schema
    // Gemini accepts: every "$ref": "#/definitions/X" is replaced by an inlined copy of that
    // definition, and metadata keywords Gemini's OpenAPI subset rejects are stripped. Types here are
    // acyclic (LayoutVariants/FloorPlanExtraction), so recursion terminates.
    private static readonly string[] DropKeywords =
        ["$schema", "definitions", "$ref", "additionalProperties", "x-enumNames"];

    private static JsonNode InlineSchema(JsonNode root)
    {
        var defs = root["definitions"]?.AsObject();
        var result = Resolve(root, defs);
        return result;

        static JsonNode Resolve(JsonNode node, JsonObject? defs)
        {
            switch (node)
            {
                case JsonObject obj:
                    // A $ref node → replace wholesale with the resolved, inlined definition.
                    if (obj.TryGetPropertyValue("$ref", out var refNode) &&
                        refNode?.GetValue<string>() is { } refPath &&
                        refPath.StartsWith("#/definitions/") &&
                        defs?[refPath["#/definitions/".Length..]] is { } target)
                    {
                        return Resolve(target.DeepClone(), defs);
                    }

                    var copy = new JsonObject();
                    foreach (var (key, value) in obj)
                    {
                        if (Array.IndexOf(DropKeywords, key) >= 0 || value is null) continue;
                        copy[key] = Resolve(value.DeepClone(), defs);
                    }
                    return copy;

                case JsonArray arr:
                    var outArr = new JsonArray();
                    foreach (var item in arr)
                        outArr.Add(item is null ? null : Resolve(item.DeepClone(), defs));
                    return outArr;

                default:
                    return node.DeepClone();
            }
        }
    }

    // Prompt-only mode (no response_format) can wrap JSON in a ```json fence; strip it.
    private static string StripFences(string s)
    {
        var t = s.Trim();
        if (!t.StartsWith("```")) return t;
        var firstNl = t.IndexOf('\n');
        if (firstNl >= 0) t = t[(firstNl + 1)..];       // drop opening ```lang line
        if (t.EndsWith("```")) t = t[..^3];               // drop closing fence
        return t.Trim();
    }

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
        - Numbers must be plain with at most 2 decimal places (e.g. 14 or 14.5). Never pad with repeated digits.
        - Keep every string value short (a few words max).

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
        - Numbers must be plain with at most 2 decimal places (e.g. 5 or 4.5). Never pad with repeated digits.
        - Keep every string value short (a few words max).

        Extracted brief (JSON):
        {1}
        """;

    private const string DirectPromptTemplate = """
        You are a floor-plan layout engine. From the user's brief below, produce {0} DISTINCT,
        buildable layout variants directly.

        Requirements for every variant:
        - Infer sensible defaults for anything the brief omits (plot size, room list, orientation).
        - Every room needs a rectangular Footprint in metres (OriginXm, OriginYm, WidthM, DepthM),
          floor-local (origin per FloorIndex), non-overlapping within a floor.
        - Give each room an AreaSqm consistent with its footprint (WidthM * DepthM), a Type, and
          PrivacyLevel/RequiresNaturalLight/RequiresEnsuite/IsPrimary where relevant.
        - Populate AdjacentTo, and add a Door on each shared wall between adjacent rooms on the same floor.
        - Vary the variants meaningfully (e.g. open-plan vs compartmentalised); set a short Label on each.
        - Numbers must be plain with at most 2 decimal places (e.g. 5 or 4.5). Never pad with repeated digits.
        - Keep every string value short (a few words max).

        User brief: "{1}"
        """;
}
