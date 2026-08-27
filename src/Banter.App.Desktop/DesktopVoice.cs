using Banter.Voice;
using Banter.Voice.OpenAI;
using Bantz.Speech;
using Bantz.Speech.Whisper;

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
    public static DesktopVoice? TryBuild(VoiceSettings settings, Action<string> warn)
    {
        var policy = ParsePolicy(settings.Readback);
        var wantsLocal = !string.Equals(settings.Engine, "remote", StringComparison.OrdinalIgnoreCase);

        Uri? endpoint = null;
        if (settings.Endpoint.Length > 0 && !Uri.TryCreate(settings.Endpoint, UriKind.Absolute, out endpoint))
        {
            warn($"'{settings.Endpoint}' is not a usable endpoint; ignoring it.");
        }

        if (!wantsLocal && endpoint is null)
        {
            warn("voice.engine is 'remote' but no endpoint is set; voice is off.");
            return null;
        }

        var options = endpoint is null ? null : new OpenAiSpeechOptions
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
        ITranscriptionEngine? transcription = null;
        if (capture is not null)
        {
            transcription = wantsLocal
                ? BuildWhisper(settings)
                : new OpenAiTranscriptionEngine(options!);

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
        if (playback is not null && options is not null)
        {
            speech = new OpenAiTextToSpeech(options);

            // Voices are configuration rather than discovery in this API, so the options' list is
            // the pool; it is read here so a server with its own voices needs no code change.
            var voices = speech.GetVoicesAsync().AsTask().GetAwaiter().GetResult();
            readback = new ReadbackSession(speech, playback, new VoiceAssignment(voices),
                new ReadbackOptions { Policy = policy });
        }
        else if (playback is not null)
        {
            // Nothing synthesises speech on this machine — Whisper only listens. Dictation still
            // works entirely locally, which is the half that matters most for privacy.
            warn("no speech endpoint, so nothing will be read aloud; dictation still works locally.");
        }
        else
        {
            warn("no speaker; dictation works but nothing will be read aloud.");
        }

        return new DesktopVoice(session, readback, policy,
            [capture as IDisposable, playback as IDisposable, transcription as IDisposable, speech]);
    }

    /// <summary>
    /// Readies the engine, reporting what it is doing. Local Whisper downloads a ~148 MB model on
    /// first use, and a client that simply did nothing for two minutes the first time the
    /// microphone was pressed would read as broken — so this runs at startup and narrates.
    /// </summary>
    public async Task PrepareAsync(Action<string> report, CancellationToken cancellationToken = default)
    {
        if (Session is null)
        {
            return;
        }

        var lastReported = -1;
        var progress = new Progress<TranscriptionInitializationProgress>(p =>
        {
            if (p.Stage is TranscriptionInitializationStage.DownloadingModel
                or TranscriptionInitializationStage.DownloadingRuntime)
            {
                // Every ten percent: enough to show it is moving, few enough not to bury the room.
                var decile = p.Percent / 10;
                if (decile == lastReported)
                {
                    return;
                }

                lastReported = decile;
                report($"{Describe(p.Stage)} {p.Percent}%");
                return;
            }

            report(Describe(p.Stage));
        });

        try
        {
            await Session.PrepareAsync(progress, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Reported, not thrown: the rest of the client works fine without a microphone.
            report($"speech engine unavailable: {ex.Message}");
        }
    }

    private static string Describe(TranscriptionInitializationStage stage) => stage switch
    {
        TranscriptionInitializationStage.DownloadingModel => "downloading the speech model",
        TranscriptionInitializationStage.DownloadingRuntime => "downloading the speech runtime",
        TranscriptionInitializationStage.LoadingModel => "loading the speech model",
        TranscriptionInitializationStage.Ready => "speech ready",
        _ => "preparing speech",
    };

    /// <summary>
    /// Local Whisper, with its model and native runtime kept under Banter's own profile folder so
    /// a Banter install does not scatter a 148 MB download into Bantz's.
    /// </summary>
    private static ITranscriptionEngine BuildWhisper(VoiceSettings settings)
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Banter", "speech");

        return new WhisperTranscriptionEngine(new WhisperOptions
        {
            ModelPathProvider = () => Path.Combine(root, "ggml-base.en.bin"),
            RuntimeRootProvider = () => Path.Combine(root, "runtime"),
            Runtime = TranscriptionRuntime.Automatic,
            Language = settings.Language.Length == 0 ? "en" : settings.Language,
        });
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
