using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Banter.Agents.Sdk;

/// <summary>A tool the model may call, described to it in OpenAI's function shape.</summary>
public sealed record ToolSpec(string Name, string Description, string JsonSchema);

/// <summary>A tool call the model asked for; <c>Arguments</c> is raw JSON.</summary>
public sealed record ToolCallRequest(string Id, string Name, string Arguments);

/// <summary>One message in an LLM conversation.</summary>
public sealed record ChatTurn(string Role, string Content)
{
    /// <summary>Tool calls this assistant turn asked for, if any.</summary>
    public IReadOnlyList<ToolCallRequest> ToolCalls { get; init; } = [];

    /// <summary>Which call a <c>tool</c> turn answers. The model matches results to calls by id,
    /// so a result sent without one is attached to the wrong call or dropped.</summary>
    public string? ToolCallId { get; init; }

    public static ChatTurn System(string content) => new("system", content);
    public static ChatTurn User(string content) => new("user", content);
    public static ChatTurn Assistant(string content) => new("assistant", content);

    /// <summary>The assistant turn that asked for tools, replayed back into the conversation.</summary>
    public static ChatTurn AssistantCalls(string content, IReadOnlyList<ToolCallRequest> calls) =>
        new("assistant", content) { ToolCalls = calls };

    /// <summary>A tool result, as the model expects to read one.</summary>
    public static ChatTurn Tool(string toolCallId, string content) =>
        new("tool", content) { ToolCallId = toolCallId };
}

/// <summary>
/// What a room agent needs from a model: turns in, text out, a chunk at a time.
///
/// <para>The seam exists because not every backend is an HTTP endpoint. A CLI-driven one is a
/// subprocess with a different protocol entirely, and the agent above this should not know which
/// it is talking to.</para>
/// </summary>
public interface IChatModel : IDisposable
{
    /// <summary>
    /// Stream a reply, yielding content as it arrives. Any tool calls the model asks for are
    /// accumulated into <paramref name="toolCalls"/> by the time the enumeration ends — a
    /// collection parameter rather than a return value, because an async iterator cannot have one.
    ///
    /// <para>A backend that owns its own tools ignores both tool arguments. That is not a gap: in
    /// this suite tools run server-side under per-agent grants and are announced in the room, so a
    /// backend bringing its own would be doing them unaudited.</para>
    /// </summary>
    IAsyncEnumerable<string> StreamAsync(
        IReadOnlyList<ChatTurn> messages,
        IReadOnlyList<ToolSpec> tools,
        ICollection<ToolCallRequest>? toolCalls,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// A minimal streaming client for the OpenAI <c>/chat/completions</c> shape — enough for LM
/// Studio, Ollama's compatibility endpoint, vLLM, or OpenAI itself.
///
/// <para>Hand-rolled rather than pulling in an SDK: the suite is deliberately dependency-light,
/// and the streaming half of this API is a dozen lines of server-sent events.</para>
/// </summary>
public sealed class OpenAiChatClient : IChatModel
{
    private readonly HttpClient _http;
    private readonly LlmChatAgentOptions _options;

    public OpenAiChatClient(LlmChatAgentOptions options, HttpMessageHandler? handler = null)
    {
        _options = options;
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = options.Timeout;
        if (options.ApiKey.Length > 0)
        {
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
        }
    }

    /// <summary>
    /// Stream a completion, yielding content deltas as they arrive. Deltas are yielded verbatim;
    /// the caller decides how to batch them.
    /// </summary>
    public IAsyncEnumerable<string> StreamAsync(
        IReadOnlyList<ChatTurn> messages,
        CancellationToken cancellationToken = default) =>
        StreamAsync(messages, [], toolCalls: null, cancellationToken);

    /// <summary>
    /// Stream a completion that may call tools. Content deltas are yielded as before; any tool
    /// calls the model asks for are accumulated into <paramref name="toolCalls"/> by the time the
    /// enumeration ends.
    ///
    /// <para>A collection parameter rather than a return value, because an async iterator cannot
    /// have one — and the caller needs both halves: models routinely emit a sentence and a tool
    /// call in the same turn, and dropping the sentence loses the thread.</para>
    /// </summary>
    public async IAsyncEnumerable<string> StreamAsync(
        IReadOnlyList<ChatTurn> messages,
        IReadOnlyList<ToolSpec> tools,
        ICollection<ToolCallRequest>? toolCalls,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var request = new ChatRequest(
            _options.Model,
            messages.Select(ToWire).ToList(),
            _options.Temperature,
            _options.MaxOutputTokens,
            Stream: true,
            tools.Count == 0 ? null : tools.Select(ToWire).ToList());

        // Keyed by the index the server assigns: the pieces of one call arrive across many
        // chunks, and a parallel call set interleaves them.
        var pending = new SortedDictionary<int, PartialCall>();

        using var message = new HttpRequestMessage(HttpMethod.Post, Combine(_options.Endpoint, "chat/completions"))
        {
            Content = JsonContent.Create(request, options: Json),
        };

        using var response = await _http
            .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new HttpRequestException(
                $"{(int)response.StatusCode} from {_options.Endpoint}: {Truncate(body, 300)}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream);

        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;                                  // blank keep-alive or comment
            }

            var payload = line[5..].Trim();
            if (payload is "[DONE]")
            {
                // Break, not `yield break`: the tool calls assembled above still have to be
                // handed to the caller, and [DONE] is exactly when they are complete.
                break;
            }

            var delta = TryReadDelta(payload, pending);
            if (delta is { Length: > 0 })
            {
                yield return delta;
            }
        }

        if (toolCalls is not null)
        {
            // A call with no name never finished arriving; handing it on would ask the server to
            // run the empty string.
            foreach (var call in pending.Values.Where(c => c.Name.Length > 0))
            {
                toolCalls.Add(new ToolCallRequest(call.Id, call.Name, call.Arguments.ToString()));
            }
        }
    }

    /// <summary>One tool call being assembled from streamed fragments.</summary>
    private sealed class PartialCall
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public System.Text.StringBuilder Arguments { get; } = new();
    }

    /// <summary>Fold a streamed <c>tool_calls</c> fragment into the calls being assembled.</summary>
    private static void ReadToolCalls(JsonElement toolCalls, SortedDictionary<int, PartialCall> pending)
    {
        foreach (var entry in toolCalls.EnumerateArray())
        {
            var index = entry.TryGetProperty("index", out var i) && i.TryGetInt32(out var parsed) ? parsed : 0;
            if (!pending.TryGetValue(index, out var call))
            {
                pending[index] = call = new PartialCall();
            }

            if (entry.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
            {
                call.Id = id.GetString() ?? "";
            }

            if (!entry.TryGetProperty("function", out var function))
            {
                continue;
            }

            if (function.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
            {
                // Appended, not assigned: some servers split even the name across chunks.
                call.Name += name.GetString();
            }

            if (function.TryGetProperty("arguments", out var arguments) &&
                arguments.ValueKind == JsonValueKind.String)
            {
                call.Arguments.Append(arguments.GetString());
            }
        }
    }

    /// <summary>
    /// A malformed chunk should not abort a reply that is otherwise working — servers differ in
    /// what they put on the wire, and dropping one delta beats losing the whole turn.
    /// </summary>
    private static string? TryReadDelta(string payload, SortedDictionary<int, PartialCall> pending)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (!doc.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
            {
                return null;
            }

            var choice = choices[0];
            if (!choice.TryGetProperty("delta", out var delta))
            {
                return null;
            }

            if (delta.TryGetProperty("tool_calls", out var toolCalls) &&
                toolCalls.ValueKind == JsonValueKind.Array)
            {
                ReadToolCalls(toolCalls, pending);
            }

            return delta.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String
                ? content.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Join a base URL that may or may not end in a slash with a relative path.</summary>
    internal static Uri Combine(Uri endpoint, string path) =>
        new(endpoint.AbsoluteUri.TrimEnd('/') + "/" + path.TrimStart('/'));

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "...";

    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static WireMessage ToWire(ChatTurn turn) => new(
        turn.Role,
        turn.Content,
        turn.ToolCalls.Count == 0
            ? null
            : turn.ToolCalls
                .Select(c => new WireToolCall(c.Id, "function", new WireCalledFunction(c.Name, c.Arguments)))
                .ToList(),
        turn.ToolCallId);

    private static WireTool ToWire(ToolSpec tool) => new(
        "function",
        // The schema arrives as text from the server's catalogue and goes out as JSON, not as a
        // quoted string — a schema the model reads as a string is a schema it ignores.
        new WireFunction(tool.Name, tool.Description, ParseSchema(tool.JsonSchema)));

    private static JsonNode ParseSchema(string schema)
    {
        try
        {
            return JsonNode.Parse(schema) ?? EmptySchema();
        }
        catch (JsonException)
        {
            // An unusable schema should leave that one tool callable with no arguments rather
            // than fail the whole request and take every other tool down with it.
            return EmptySchema();
        }
    }

    private static JsonNode EmptySchema() =>
        new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() };

    private sealed record WireCalledFunction(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("arguments")] string Arguments);

    private sealed record WireToolCall(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("function")] WireCalledFunction Function);

    private sealed record WireFunction(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("description")] string Description,
        [property: JsonPropertyName("parameters")] JsonNode Parameters);

    private sealed record WireTool(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("function")] WireFunction Function);

    private sealed record WireMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content,
        [property: JsonPropertyName("tool_calls")] IReadOnlyList<WireToolCall>? ToolCalls,
        [property: JsonPropertyName("tool_call_id")] string? ToolCallId);

    private sealed record ChatRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("messages")] IReadOnlyList<WireMessage> Messages,
        [property: JsonPropertyName("temperature")] double Temperature,
        [property: JsonPropertyName("max_tokens")] int MaxTokens,
        [property: JsonPropertyName("stream")] bool Stream,
        [property: JsonPropertyName("tools")] IReadOnlyList<WireTool>? Tools);

    public void Dispose() => _http.Dispose();
}
