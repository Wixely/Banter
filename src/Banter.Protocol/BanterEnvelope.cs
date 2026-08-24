using MessagePack;

namespace Banter.Protocol;

/// <summary>
/// The outer frame of every BanterProtocol message: version, type, correlation ids, and the
/// serialized payload. Request/response correlation rides on <see cref="MsgId"/> /
/// <see cref="ReplyTo"/>; server pushes have no <see cref="ReplyTo"/>.
/// </summary>
[MessagePackObject]
public sealed record BanterEnvelope(
    [property: Key(0)] ushort Ver,
    [property: Key(1)] BanterMessageType Type,
    [property: Key(2)] string MsgId,
    [property: Key(3)] string? ReplyTo,
    [property: Key(4)] byte[] Payload)
{
    /// <summary>Protocol revision this library speaks. Becomes the negotiated CupriMark ordinal later.</summary>
    public const ushort CurrentVersion = 1;

    public static string NewMsgId() => Guid.NewGuid().ToString("N");
}
