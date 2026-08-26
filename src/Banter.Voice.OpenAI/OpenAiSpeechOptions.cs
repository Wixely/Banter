namespace Banter.Voice.OpenAI;

/// <summary>
/// Where speech is sent and under what name (PLAN §6). One set of options covers OpenAI, Qwen
/// through DashScope's OpenAI-compatible surface, and every local server that speaks the same
/// shape (Speaches, LocalAI, vLLM) — the only differences are the base URL, the key, and the
/// model names, which is exactly why the plan calls for one well-built adapter rather than three.
/// </summary>
public sealed record OpenAiSpeechOptions
{
    /// <summary>Base URL up to and including the version segment, e.g. <c>https://api.openai.com/v1</c>.</summary>
    public required Uri Endpoint { get; init; }

    /// <summary>Bearer token. Empty for a local server that wants none — the header is then
    /// omitted entirely rather than sent blank, which some servers reject.</summary>
    public string ApiKey { get; init; } = "";

    public string TranscriptionModel { get; init; } = "whisper-1";

    /// <summary>
    /// BCP-47 hint, or null to let the engine detect. Worth setting: detection on a short
    /// utterance is a coin flip, and a misdetected language produces fluent nonsense rather than
    /// an error.
    /// </summary>
    public string? Language { get; init; }

    /// <summary>
    /// Vocabulary hint passed to the engine. This is where room nicknames and agent names belong:
    /// they are the words an acoustic model has never seen and will confidently replace with
    /// something plausible.
    /// </summary>
    public string? Prompt { get; init; }

    /// <summary>
    /// A ceiling for one request. Generous, because a cold local server loading a model on first
    /// call is slow once and fast afterwards, and failing that first call looks like a broken
    /// configuration.
    /// </summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(2);

    public string SpeechModel { get; init; } = "tts-1";

    /// <summary>Used when a <see cref="SpeechRequest"/> names none.</summary>
    public string DefaultVoice { get; init; } = "alloy";

    /// <summary>
    /// What to ask the server to return. <see cref="SpeechAudioFormat.Pcm"/> is the least work —
    /// no header, no decoding — but not every server offers it, and a WAV carries the sample rate
    /// with it rather than relying on <see cref="PcmSampleRate"/> being right.
    /// </summary>
    public SpeechAudioFormat Format { get; init; } = SpeechAudioFormat.Pcm;

    /// <summary>
    /// The rate raw PCM comes back at, since nothing in the reply states it. OpenAI's <c>pcm</c>
    /// is 24 kHz mono signed 16-bit; a server that differs needs this set, or everything it says
    /// plays at the wrong pitch. Ignored for WAV, which says so itself.
    /// </summary>
    public int PcmSampleRate { get; init; } = 24000;

    /// <summary>
    /// The voices offered for per-sender assignment (§6). Configuration rather than discovery:
    /// the speech API has no endpoint that lists voices, so a server with its own set needs them
    /// named here. The default is OpenAI's long-standing six.
    /// </summary>
    public IReadOnlyList<VoiceDescriptor> Voices { get; init; } =
    [
        new("alloy"), new("echo"), new("fable"), new("onyx"), new("nova"), new("shimmer"),
    ];
}

/// <summary>
/// Reply encodings this adapter can turn back into samples. Compressed formats are deliberately
/// absent: decoding MP3 or Opus needs a codec, and the suite is C#-only by constraint (PLAN §1).
/// </summary>
public enum SpeechAudioFormat
{
    /// <summary>Headerless signed 16-bit samples at <see cref="OpenAiSpeechOptions.PcmSampleRate"/>.</summary>
    Pcm,

    /// <summary>RIFF/WAVE; the header states the rate, so it wins over the configured one.</summary>
    Wav,
}
