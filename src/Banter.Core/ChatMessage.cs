namespace Banter.Core;

/// <summary>A persisted room message — the domain twin of the protocol's MsgPayload, kept
/// separate so storage never depends on wire contracts.</summary>
public sealed record ChatMessage(
    string MessageId,
    string Room,
    string Sender,
    string Text,
    long Timestamp,
    string? FileId)
{
    /// <summary>
    /// The message this one answers, or null. Stored rather than live-only: an agent reading a
    /// room back after a restart needs to know what "the second one" referred to just as much as
    /// one that was listening at the time.
    /// </summary>
    public string? ReplyTo { get; init; }

    /// <summary>When the author last changed it, or null if never. Clients mark those "edited";
    /// a reader deserves to know the words are not the ones originally said.</summary>
    public long? EditedAt { get; init; }

    /// <summary>
    /// When it was taken back, or null. A deleted message keeps its row and loses its
    /// <see cref="Text"/> — the words are genuinely gone, the fact of them is not, because a row
    /// that vanished would break history cursors that point at it.
    /// </summary>
    public long? DeletedAt { get; init; }
}
