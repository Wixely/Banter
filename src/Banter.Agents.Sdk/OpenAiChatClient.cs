using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Banter.Agents.Sdk;

/// <summary>One message in an LLM conversation.</summary>
public sealed record ChatTurn(string Role, string Content)
{
    public static ChatTurn System(string content) => new("system", content);
    public static ChatTurn User(string content) => new("user", content);
    public static ChatTurn Assistant(string content) => new("assistant", content);
}

/// <summary>
/// A minimal streaming client for the OpenAI <c>/chat/completions</c> shape — enough for LM
/// Studio, Ollama's compatibility endpoint, vLLM, or OpenAI itself.
///
/// <para>Hand-rolled rather than pulling in an SDK: the suite is deliberately dependency-light,
/// and the streaming half of this API is a dozen lines of server-sent events.</para>
/// </summary>
public sealed class OpenAiChatClient : IDisposable
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
    public async IAsyncEnumerable<string> StreamAsync(
        IReadOnlyList<ChatTurn> messages,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var request = new ChatRequest(
            _options.Model,
            messages.Select(m => new WireMessage(m.Role, m.Content)).ToList(),
            _options.Temperature,
            _options.MaxOutputTokens,
            Stream: true);

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
                yield break;
            }

            var delta = TryReadDelta(payload);
            if (delta is { Length: > 0 })
            {
                yield return delta;
            }
        }
    }

    /// <summary>
    /// A malformed chunk should not abort a reply that is otherwise working — servers differ in
    /// what they put on the wire, and dropping one delta beats losing the whole turn.
    /// </summary>
    private static string? TryReadDelta(string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (!doc.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
            {
                return null;
            }

            var choice = choices[0];
            if (choice.TryGetProperty("delta", out var delta) &&
                delta.TryGetProperty("content", out var content) &&
                content.ValueKind == JsonValueKind.String)
            {
                return content.GetString();
            }

            return null;
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

    private sealed record WireMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content);

    private sealed record ChatRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("messages")] IReadOnlyList<WireMessage> Messages,
        [property: JsonPropertyName("temperature")] double Temperature,
        [property: JsonPropertyName("max_tokens")] int MaxTokens,
        [property: JsonPropertyName("stream")] bool Stream);

    public void Dispose() => _http.Dispose();
}
