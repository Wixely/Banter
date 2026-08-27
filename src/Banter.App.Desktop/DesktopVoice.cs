using Banter.Voice;
using Banter.Voice.OpenAI;
using Bantz.Speech;

namespace Banter.App.Desktop;

/// <summary>
/// Assembles the desktop's voice stack from settings and whatever devices this machine has
/// (PLAN §6).
///
/// <para>Every part is optional and independently so: a machine with a microphone but no speaker
/// dictates and stays quiet, one with a speaker but no microphone is read aloud to, and one with
/// neither runs exactly as it did before voice existed. That is why so much of this returns null
/// instead of throwing — none of these are error conditions, they are ordinary machines.</para>
/// </summary>
public sealed class DesktopVoice : IAsyncDisposable
{
    private readonly IDisposable?[] _devices;

    private DesktopVoice(
        VoiceSession? session,
        ReadbackSession? readback,
        ReadbackPolicy policy,
        IDisposable?[] devices)
    {
        Session = session;
        Readback = readback;
        Policy = policy;
        _devices = devices;
    }

    public VoiceSession? Session { get; }

    public ReadbackSession? Readback { get; }

    public ReadbackPolicy Policy { get; }

    /// <summary>
    /// Builds what this machine can manage, or null when it can manage nothing. Reasons go to
    /// <paramref name="warn"/> rather than into an exception: "no speech server configured" is a
    /// thing to mention on the way past, not a reason to refuse to start a chat client.
    /// </summary>
    public static DesktopVoice? TryBuild(VoiceSettings settings, ChatViewModel viewModel, Action<string> warn)
    {
        var policy = ParsePolicy(settings.Readback);

        if (settings.Endpoint.Length == 0)
        {
            // Local Whisper is meant to fill this gap on desktop and has not landed yet, so for
            // now an unconfigured client is a silent one — said out loud rather than left as a
            // mystery missing button.
            warn("no speech endpoint configured; set voice.endpoint in settings.json to enable it.");
            return null;
        }

        if (!Uri.TryCreate(settings.Endpoint, UriKind.Absolute, out var endpoint))
        {
            warn($"'{settings.Endpoint}' is not a usable endpoint; voice is off.");
            return null;
        }

        var options = new OpenAiSpeechOptions
        {
            Endpoint = endpoint,
            ApiKey = Environment.GetEnvironmentVariable("BANTER_SPEECH_KEY") ?? "",
            TranscriptionModel = settings.TranscriptionModel,
            SpeechModel = settings.SpeechModel,
            Language = settings.Language.Length == 0 ? null : settings.Language,
            Prompt = settings.Vocabulary.Length == 0 ? null : settings.Vocabulary,
        };

        var capture = DesktopAudio.TryCreateCapture();
        var playback = DesktopAudio.TryCreatePlayback();

        if (capture is null && playback is null)
        {
            warn("no audio devices; voice is off.");
            return null;
        }

        VoiceSession? session = null;
        OpenAiTranscriptionEngine? transcription = null;
        if (capture is not null)
        {
            transcription = new OpenAiTranscriptionEngine(options);
            session = new VoiceSession(capture, transcription, new VoiceSessionOptions
            {
                Mode = settings.AlwaysListening
                    ? VoiceCaptureMode.AlwaysListening
                    : VoiceCaptureMode.PushToTalk,
            });
        }
        else
        {
            warn("no microphone; the room can be read aloud but not spoken to.");
        }

        ReadbackSession? readback = null;
        OpenAiTextToSpeech? speech = null;
        if (playback is not null)
        {
            speech = new OpenAiTextToSpeech(options);

            // Voices are configuration rather than discovery in this API, so the options' list is
            // the pool; it is read here so a server with its own voices needs no code change.
            var voices = speech.GetVoicesAsync().AsTask().GetAwaiter().GetResult();
            readback = new ReadbackSession(speech, playback, new VoiceAssignment(voices),
                new ReadbackOptions { Policy = policy });
        }
        else
        {
            warn("no speaker; dictation works but nothing will be read aloud.");
        }

        return new DesktopVoice(session, readback, policy,
            [capture as IDisposable, playback as IDisposable, transcription, speech]);
    }

    private static ReadbackPolicy ParsePolicy(string value) => value.ToLowerInvariant() switch
    {
        "off" or "none" => ReadbackPolicy.Off,
        "everyone" or "all" => ReadbackPolicy.Everyone,
        _ => ReadbackPolicy.AgentsOnly,
    };

    public async ValueTask DisposeAsync()
    {
        if (Session is not null)
        {
            await Session.DisposeAsync().ConfigureAwait(false);
        }

        if (Readback is not null)
        {
            await Readback.DisposeAsync().ConfigureAwait(false);
        }

        foreach (var device in _devices)
        {
            device?.Dispose();
        }
    }
}
