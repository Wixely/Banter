using System.Diagnostics;
using System.Text.Json.Nodes;
using Bantz.Speech;

namespace Banter.Voice.Wyoming;

/// <summary>
/// Transcription through a Wyoming ASR service — faster-whisper being the usual one (PLAN §6).
///
/// <para>Implements the same <see cref="ITranscriptionEngine"/> as the local Whisper engine and
/// the OpenAI-compatible adapter, so which of the three a head uses is configuration rather than
/// a third code path.</para>
/// </summary>
public sealed class WyomingTranscriptionEngine(WyomingOptions options) : ITranscriptionEngine
{
    private string _lastRun = "never";

    /// <summary>
    /// True, for the same reason the HTTP adapter's is: nothing here installs, and an unreachable
    /// service is a fact about a call rather than about readiness.
    /// </summary>
    public bool IsReady => true;

    /// <summary>
    /// A no-op. <b>ValueTask, matching the interface exactly</b> — <see cref="ITranscriptionEngine"/>
    /// gives this member a default implementation, so a signature that is merely close compiles
    /// clean and quietly leaves every interface-typed caller talking to the default instead.
    /// </summary>
    public ValueTask InitializeAsync(
        IProgress<TranscriptionInitializationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Report(new TranscriptionInitializationProgress(TranscriptionInitializationStage.Ready));
        return ValueTask.CompletedTask;
    }

    public async Task<TranscriptionResult> TranscribeAsync(
        PcmAudio audio,
        CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp();

        await using var connection = await WyomingConnection
            .ConnectAsync(options.Host, options.Port, options.Timeout, cancellationToken)
            .ConfigureAwait(false);

        var request = new JsonObject();
        if (options.Name is { Length: > 0 })
        {
            request["name"] = options.Name;
        }

        if (options.Language is { Length: > 0 })
        {
            request["language"] = options.Language;
        }

        await connection.SendAsync(WyomingEvent.Of("transcribe", request), cancellationToken).ConfigureAwait(false);

        var format = new JsonObject
        {
            ["rate"] = audio.SampleRate,
            ["width"] = 2,                                  // signed 16-bit, the only format in play
            ["channels"] = audio.Channels,
        };

        await connection.SendAsync(WyomingEvent.Of("audio-start", format), cancellationToken).ConfigureAwait(false);

        for (var offset = 0; offset < audio.Data.Length; offset += options.ChunkBytes)
        {
            var length = Math.Min(options.ChunkBytes, audio.Data.Length - offset);

            // The format rides on every chunk, not just audio-start: services read it per chunk,
            // and one that missed the start event would otherwise guess.
            var chunk = new WyomingEvent("audio-chunk", (JsonObject)format.DeepClone(), audio.Data.Slice(offset, length));
            await connection.SendAsync(chunk, cancellationToken).ConfigureAwait(false);
        }

        await connection.SendAsync(WyomingEvent.Of("audio-stop"), cancellationToken).ConfigureAwait(false);

        while (await connection.ReceiveAsync(cancellationToken).ConfigureAwait(false) is { } reply)
        {
            if (reply.Type != "transcript")
            {
                continue;                                   // services narrate; only the transcript matters
            }

            var elapsed = Stopwatch.GetElapsedTime(started);
            _lastRun = $"{elapsed.TotalSeconds:0.0} s for {audio.Duration.TotalSeconds:0.0} s of audio";
            return new TranscriptionResult(reply.Text?.Trim() ?? "", options.Language);
        }

        throw new IOException($"{options} closed the connection without returning a transcript.");
    }

    public TranscriptionDiagnostics GetDiagnostics() => new(
        IsReady,
        "wyoming",
        typeof(WyomingTranscriptionEngine).Assembly.GetName().Version?.ToString() ?? "",
        "remote",
        options.Language ?? "auto",
        options.Name ?? "service default",
        options.ToString(),
        _lastRun);
}
