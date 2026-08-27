namespace Banter.Voice.Wyoming;

/// <summary>
/// Where a Wyoming service lives and what to ask it for (PLAN §6). Wyoming is one service per
/// job — faster-whisper listening on one port, Piper speaking on another — so transcription and
/// speech each take their own options rather than sharing a base URL the way the
/// OpenAI-compatible adapter does.
/// </summary>
public sealed record WyomingOptions
{
    public required string Host { get; init; }

    public required int Port { get; init; }

    /// <summary>
    /// The service's model or voice name, or null for whatever it defaults to. For a transcriber
    /// this is a model (<c>faster-whisper-medium</c>); for a speaker it is a voice
    /// (<c>en_US-lessac-medium</c>).
    /// </summary>
    public string? Name { get; init; }

    /// <summary>BCP-47 hint, or null to let the service decide.</summary>
    public string? Language { get; init; }

    /// <summary>A speaker within a multi-speaker voice, where the service offers them.</summary>
    public string? Speaker { get; init; }

    /// <summary>
    /// How long to wait for the socket and for the reply. Generous for the same reason the
    /// HTTP adapter's is: a service loading a model on its first request is slow exactly once.
    /// </summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Audio sent per <c>audio-chunk</c>. Small enough that a service can begin work before the
    /// clip has finished arriving, large enough not to be mostly framing.
    /// </summary>
    public int ChunkBytes { get; init; } = 8192;

    /// <summary>
    /// The voices offered for per-sender assignment. Wyoming does advertise its voices through
    /// <c>describe</c>, but that costs a round trip on every start-up and the answer rarely
    /// changes, so it is configuration here as it is for the OpenAI adapter.
    /// </summary>
    public IReadOnlyList<VoiceDescriptor> Voices { get; init; } = [];

    public override string ToString() => $"{Host}:{Port}";
}
