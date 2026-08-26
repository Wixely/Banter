using Bantz.Speech;

namespace Banter.Voice;

/// <summary>A voice a backend can speak in.</summary>
/// <param name="Id">What <see cref="SpeechRequest.Voice"/> takes — the backend's own identifier.</param>
/// <param name="Name">A label for a picker, when the backend offers one distinct from the id.</param>
/// <param name="Language">BCP-47 tag, where the backend declares one.</param>
public sealed record VoiceDescriptor(string Id, string? Name = null, string? Language = null)
{
    public string DisplayName => Name ?? Id;
}

/// <param name="Text">What to say.</param>
/// <param name="Voice">A <see cref="VoiceDescriptor.Id"/>, or null for the backend default.</param>
/// <param name="Speed">Rate multiplier; 1.0 is the backend's natural pace.</param>
public sealed record SpeechRequest(string Text, string? Voice = null, double Speed = 1.0);

/// <summary>
/// Turns text into audio (PLAN §6). The mirror of <see cref="ITranscriptionEngine"/>, and
/// deliberately shaped like it — same readiness/initialise pattern — so a head can hold one
/// configuration screen for both directions rather than two that disagree.
/// </summary>
public interface ITextToSpeech
{
    /// <summary>Whether the backend has everything it needs to speak.</summary>
    bool IsReady { get; }

    /// <summary>Connects, authenticates or loads whatever speaking requires.</summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The voices available. Per-sender voice assignment (§6, "so multi-agent rooms are
    /// distinguishable by ear") draws from this, so a backend that offers only one still returns
    /// a single-entry list rather than throwing.
    /// </summary>
    ValueTask<IReadOnlyList<VoiceDescriptor>> GetVoicesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Speaks <paramref name="request"/>, yielding audio as it is produced.
    ///
    /// <para>Streamed rather than returned whole because §6 speaks a streamed agent message
    /// sentence-by-sentence as its deltas complete: waiting for a full synthesis before the first
    /// sound adds that wait to every sentence, and the room hears an agent that pauses before
    /// each one.</para>
    /// </summary>
    IAsyncEnumerable<PcmAudio> SynthesizeAsync(SpeechRequest request, CancellationToken cancellationToken = default);
}
