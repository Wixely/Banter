using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using Bantz.Speech;

namespace Banter.Voice.OpenAI;

/// <summary>
/// Transcription over the OpenAI <c>/audio/transcriptions</c> shape.
///
/// <para>Implements the same <see cref="ITranscriptionEngine"/> that Bantz's local Whisper engine
/// does (PLAN §6a), so choosing between "on this machine" and "on a server" is one line of
/// configuration in a head rather than a second code path through the client.</para>
///
/// <para>Hand-rolled against <c>HttpClient</c>, matching <c>OpenAiChatClient</c>: the request is a
/// multipart form and the reply is one JSON object, which is less code than taking an SDK
/// dependency for it.</para>
/// </summary>
public sealed class OpenAiTranscriptionEngine : ITranscriptionEngine, IDisposable
{
    private readonly OpenAiSpeechOptions _options;
    private readonly HttpClient _http;
    private string _lastRun = "never";

    public OpenAiTranscriptionEngine(OpenAiSpeechOptions options, HttpMessageHandler? handler = null)
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
    /// Always true. A remote engine has nothing to install, and reporting "not ready" until some
    /// probe succeeded would make an offline server look like a misconfigured client — the two
    /// need different answers from the user, so the failure belongs on the call that failed.
    /// </summary>
    public bool IsReady => true;

    /// <summary>
    /// A no-op that reports completion. The contract exists for engines that download a model;
    /// here the model is already somewhere else, which is the entire point of this adapter.
    /// </summary>
    public Task InitializeAsync(
        IProgress<TranscriptionInitializationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Report(new TranscriptionInitializationProgress(TranscriptionInitializationStage.Ready));
        return Task.CompletedTask;
    }

    public async Task<TranscriptionResult> TranscribeAsync(PcmAudio audio, CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp();

        using var form = new MultipartFormDataContent();

        // CreateWaveStream hands over a seekable stream we own; the multipart content disposes it
        // with the form, so it is added rather than wrapped in its own using.
        var wave = new StreamContent(audio.CreateWaveStream());
        wave.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(wave, "file", "audio.wav");
        form.Add(new StringContent(_options.TranscriptionModel), "model");
        form.Add(new StringContent("json"), "response_format");

        if (!string.IsNullOrWhiteSpace(_options.Language))
        {
            form.Add(new StringContent(_options.Language), "language");
        }

        if (!string.IsNullOrWhiteSpace(_options.Prompt))
        {
            form.Add(new StringContent(_options.Prompt), "prompt");
        }

        using var response = await _http
            .PostAsync(Combine(_options.Endpoint, "audio/transcriptions"), form, cancellationToken)
            .ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"{(int)response.StatusCode} from {_options.Endpoint}: {Truncate(body, 300)}");
        }

        var elapsed = Stopwatch.GetElapsedTime(started);
        _lastRun = $"{elapsed.TotalSeconds:0.0} s for {audio.Duration.TotalSeconds:0.0} s of audio";

        return Parse(body, _options.Language);
    }

    public TranscriptionDiagnostics GetDiagnostics() => new(
        IsReady,
        "openai-compatible",
        typeof(OpenAiTranscriptionEngine).Assembly.GetName().Version?.ToString() ?? "",
        "remote",
        _options.Language ?? "auto",
        _options.TranscriptionModel,
        _options.Endpoint.ToString(),
        _lastRun);

    /// <summary>
    /// Reads the reply. Servers in this family agree on <c>text</c> and disagree about everything
    /// else, so only <c>text</c> is required and a missing <c>language</c> falls back to what was
    /// asked for rather than failing a transcript that arrived intact.
    /// </summary>
    private static TranscriptionResult Parse(string body, string? requested)
    {
        JsonElement root;
        try
        {
            root = JsonDocument.Parse(body).RootElement;
        }
        catch (JsonException e)
        {
            throw new HttpRequestException($"Unreadable transcription reply: {Truncate(body, 300)}", e);
        }

        if (!root.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String)
        {
            throw new HttpRequestException($"Transcription reply had no text: {Truncate(body, 300)}");
        }

        var language = root.TryGetProperty("language", out var l) && l.ValueKind == JsonValueKind.String
            ? l.GetString()
            : requested;

        return new TranscriptionResult(text.GetString()?.Trim() ?? "", language);
    }

    internal static Uri Combine(Uri endpoint, string path) =>
        new(endpoint.AbsoluteUri.TrimEnd('/') + "/" + path.TrimStart('/'));

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "...";

    public void Dispose() => _http.Dispose();
}
