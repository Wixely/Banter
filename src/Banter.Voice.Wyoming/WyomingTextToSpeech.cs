using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Bantz.Speech;

namespace Banter.Voice.Wyoming;

/// <summary>
/// Speech through a Wyoming TTS service — Piper being the usual one (PLAN §6).
///
/// <para>Audio is yielded as its <c>audio-chunk</c> events arrive, so playback starts while the
/// service is still speaking rather than after it has finished.</para>
/// </summary>
public sealed class WyomingTextToSpeech(WyomingOptions options) : ITextToSpeech
{
    public bool IsReady => true;

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public ValueTask<IReadOnlyList<VoiceDescriptor>> GetVoicesAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(options.Voices);

    public async IAsyncEnumerable<PcmAudio> SynthesizeAsync(
        SpeechRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Text))
        {
            yield break;
        }

        await using var connection = await WyomingConnection
            .ConnectAsync(options.Host, options.Port, options.Timeout, cancellationToken)
            .ConfigureAwait(false);

        var synthesize = new JsonObject { ["text"] = request.Text };

        var voiceName = request.Voice ?? options.Name;
        if (voiceName is { Length: > 0 } || options.Speaker is { Length: > 0 })
        {
            var voice = new JsonObject();
            if (voiceName is { Length: > 0 })
            {
                voice["name"] = voiceName;
            }

            if (options.Language is { Length: > 0 })
            {
                voice["language"] = options.Language;
            }

            if (options.Speaker is { Length: > 0 })
            {
                voice["speaker"] = options.Speaker;
            }

            synthesize["voice"] = voice;
        }

        await connection.SendAsync(WyomingEvent.Of("synthesize", synthesize), cancellationToken).ConfigureAwait(false);

        // What audio-start declared. Chunks usually repeat it, but a service that only says it
        // once still has to be understood, so it is remembered here.
        var rate = PcmAudio.SpeechSampleRate;
        var channels = PcmAudio.SpeechChannels;

        while (await connection.ReceiveAsync(cancellationToken).ConfigureAwait(false) is { } reply)
        {
            switch (reply.Type)
            {
                case "audio-start":
                    rate = reply.Int("rate") ?? rate;
                    channels = reply.Int("channels") ?? channels;
                    Ensure16Bit(reply);
                    break;

                case "audio-chunk":
                    rate = reply.Int("rate") ?? rate;
                    channels = reply.Int("channels") ?? channels;
                    Ensure16Bit(reply);
                    if (reply.Payload.Length > 0)
                    {
                        yield return new PcmAudio(reply.Payload, rate, channels);
                    }

                    break;

                case "audio-stop":
                    yield break;
            }
        }
    }

    /// <summary>
    /// Refuses anything but signed 16-bit. Playing 32-bit samples as 16-bit is not quiet or
    /// distorted, it is white noise at full volume through whatever the speaker is.
    /// </summary>
    private static void Ensure16Bit(WyomingEvent e)
    {
        if (e.Int("width") is { } width && width != 2)
        {
            throw new IOException($"Wyoming service sent {width * 8}-bit audio; this adapter reads signed 16-bit.");
        }
    }
}
