namespace Banter.Core;

/// <summary>A persisted room message — the domain twin of the protocol's MsgPayload, kept
/// separate so storage never depends on wire contracts.</summary>
public sealed record ChatMessage(
    string MessageId,
    string Room,
    string Sender,
    string Text,
    long Timestamp,
    string? FileId);
