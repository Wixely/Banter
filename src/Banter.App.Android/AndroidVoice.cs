using Banter.App;
using Banter.Voice;
using Banter.Voice.OpenAI;
using Banter.Voice.Wyoming;
using Bantz.Speech;

namespace Banter.App.Android;

/// <summary>
/// The phone's voice stack (PLAN §6a).
///
/// <para>Remote engines only, and that is the plan's decision rather than a shortcut: a 148 MB
/// model and native inference are unattractive on a phone, so local Whisper is the desktop default
/// and the remote ones are the default here. Either the OpenAI-compatible endpoint or a Wyoming
/// service will do, and a phone with neither configured simply has no microphone button.</para>
/// </summary>
public sealed class AndroidVoice : IAsyncDisposable
{
    private readonly IDisposable?[] _devices;

    private AndroidVoice(
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
    /// Builds what the settings ask for, or null when they ask for nothing reachable. Reasons go
    /// to <paramref name="warn"/>: an unconfigured phone is an ordinary phone, not an error.
    /// </summary>
    public static AndroidVoice? TryBuild(VoiceSettings settings, Action<string> warn)
    {
        var policy = ParsePolicy(settings.Readback);

        Uri? endpoint = null;
        if (settings.Endpoint.Length > 0 && !Uri.TryCreate(settings.Endpoint, UriKind.Absolute, out endpoint))
        {
            warn($"'{settings.Endpoint}' is not a usable endpoint; ignoring it.");
        }

        var openAi = endpoint is null ? null : new OpenAiSpeechOptions
        {
            Endpoint = endpoint,
            TranscriptionModel = settings.TranscriptionModel,
            SpeechModel = settings.SpeechModel,
            Language = settings.Language.Length == 0 ? null : settings.Language,
            Prompt = settings.Vocabulary.Length == 0 ? null : settings.Vocabulary,
        };

        var hasWyomingAsr = TryEndpoint(settings.WyomingAsr, out var asrHost, out var asrPort);
        var hasWyomingTts = TryEndpoint(settings.WyomingTts, out var ttsHost, out var ttsPort);

        ITranscriptionEngine? transcription =
            hasWyomingAsr ? new WyomingTranscriptionEngine(new WyomingOptions
            {
                Host = asrHost,
                Port = asrPort,
                Language = settings.Language.Length == 0 ? null : settings.Language,
            })
            : openAi is not null ? new OpenAiTranscriptionEngine(openAi)
            : null;

        ITextToSpeech? speaker =
            hasWyomingTts ? new WyomingTextToSpeech(new WyomingOptions
            {
                Host = ttsHost,
                Port = ttsPort,
                Language = settings.Language.Length == 0 ? null : settings.Language,
                Voices = [.. settings.WyomingVoices.Select(v => new VoiceDescriptor(v))],
            })
            : openAi is not null ? new OpenAiTextToSpeech(openAi)
            : null;

        if (transcription is null && speaker is null)
        {
            warn("no speech service configured; voice is off. Set voice.endpoint or voice.wyomingAsr.");
            return null;
        }

        VoiceSession? session = null;
        AndroidVoiceCapture? capture = null;
        if (transcription is not null)
        {
            capture = new AndroidVoiceCapture();
            session = new VoiceSession(capture, transcription, new VoiceSessionOptions
            {
                Mode = settings.AlwaysListening
                    ? VoiceCaptureMode.AlwaysListening
                    : VoiceCaptureMode.PushToTalk,
            });
        }

        ReadbackSession? readback = null;
        AndroidAudioPlayback? playback = null;
        if (speaker is not null)
        {
            playback = new AndroidAudioPlayback();
            var voices = speaker.GetVoicesAsync().AsTask().GetAwaiter().GetResult();
            readback = new ReadbackSession(speaker, playback, new VoiceAssignment(voices),
                new ReadbackOptions { Policy = policy });
        }

        return new AndroidVoice(session, readback, policy,
            [capture, playback, transcription as IDisposable, speaker as IDisposable]);
    }

    private static bool TryEndpoint(string value, out string host, out int port)
    {
        host = "";
        port = 0;
        var colon = value.LastIndexOf(':');
        if (colon <= 0 || !int.TryParse(value.AsSpan(colon + 1), out port) || port is < 1 or > 65535)
        {
            return false;
        }

        host = value[..colon];
        return host.Length > 0;
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
