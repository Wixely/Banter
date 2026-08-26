using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Bantz.Speech;

namespace Banter.Voice.OpenAI;

/// <summary>
/// Speech over the OpenAI <c>/audio/speech</c> shape — the same one Qwen's qwen-tts and local
/// servers such as Speaches and Kokoro expose, so the single adapter of §6 covers the outbound
/// direction too.
/// </summary>
public sealed class OpenAiTextToSpeech : ITextToSpeech, IDisposable
{
    /// <summary>
    /// How much of the reply is turned into one buffer. Small enough that playback starts almost
    /// at once, large enough that a sentence is not a thousand allocations.
    /// </summary>
    private const int ChunkBytes = 8192;

    /// <summary>Room for a header carrying <c>LIST</c> or <c>fact</c> chunks ahead of the audio.</summary>
    private const int HeaderBytes = 512;

    private readonly OpenAiSpeechOptions _options;
    private readonly HttpClient _http;

    public OpenAiTextToSpeech(OpenAiSpeechOptions options, HttpMessageHandler? handler = null)
    {
        _options = options;
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = options.Timeout;
        if (options.ApiKey.Length > 0)
        {
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
        }
    }

    /// <summary>True for the same reason the transcription engine's is: there is nothing here to
    /// install, and an unreachable server is a fact about a call, not about readiness.</summary>
    public bool IsReady => true;

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public ValueTask<IReadOnlyList<VoiceDescriptor>> GetVoicesAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(_options.Voices);

    public async IAsyncEnumerable<PcmAudio> SynthesizeAsync(
        SpeechRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Text))
        {
            yield break;
        }

        var wire = new SpeechWireRequest(
            _options.SpeechModel,
            request.Text,
            request.Voice ?? _options.DefaultVoice,
            _options.Format == SpeechAudioFormat.Wav ? "wav" : "pcm",
            request.Speed);

        using var message = new HttpRequestMessage(HttpMethod.Post, Combine(_options.Endpoint, "audio/speech"))
        {
            Content = JsonContent.Create(wire),
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

        var sampleRate = _options.PcmSampleRate;
        var channels = PcmAudio.SpeechChannels;
        var buffer = new byte[ChunkBytes];
        var carried = 0;

        if (_options.Format == SpeechAudioFormat.Wav)
        {
            var header = new byte[HeaderBytes];
            var read = await stream
                .ReadAtLeastAsync(header, WaveHeader.MinimumBytes, throwOnEndOfStream: false, cancellationToken)
                .ConfigureAwait(false);

            if (!WaveHeader.TryParse(header.AsSpan(0, read), out var format, out var dataOffset))
            {
                throw new HttpRequestException($"Speech reply was not a readable WAV ({read} bytes).");
            }

            if (format.BitsPerSample != 16)
            {
                throw new HttpRequestException(
                    $"Speech reply is {format.BitsPerSample}-bit; this adapter reads signed 16-bit.");
            }

            sampleRate = format.SampleRate;
            channels = Math.Max(1, format.Channels);

            // Whatever followed the header in that first read is already audio.
            carried = read - dataOffset;
            header.AsSpan(dataOffset, carried).CopyTo(buffer);
        }

        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(carried), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            var available = carried + read;

            // Never split a sample across two buffers: a consumer measuring one would read the
            // halves of a sample as a sample of its own, which is a click on every chunk edge.
            var whole = available - (available % 2);
            if (whole > 0)
            {
                yield return new PcmAudio(buffer.AsSpan(0, whole).ToArray(), sampleRate, channels);
            }

            carried = available - whole;
            if (carried > 0)
            {
                buffer[0] = buffer[whole];
            }
        }
    }

    private sealed record SpeechWireRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("input")] string Input,
        [property: JsonPropertyName("voice")] string Voice,
        [property: JsonPropertyName("response_format")] string ResponseFormat,
        [property: JsonPropertyName("speed")] double Speed);

    private static Uri Combine(Uri endpoint, string path) =>
        new(endpoint.AbsoluteUri.TrimEnd('/') + "/" + path.TrimStart('/'));

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "...";

    public void Dispose() => _http.Dispose();
}
