using MessagePack;

namespace Banter.Protocol;

// v1 payload contracts for the session, presence/rooms, chat, and streaming areas.
// File, agent-control, and task payloads land with their phases; their message types and
// number ranges are already reserved in BanterMessageType.

// ---- Session ----

/// <summary>First message on a channel, both directions. Carries the CupriMark negotiation
/// payload once catalogues are adopted; until then <see cref="Capabilities"/> is a free-form list.</summary>
[MessagePackObject]
public sealed record HelloPayload(
    [property: Key(0)] string ClientName,
    [property: Key(1)] string ClientVersion,
    [property: Key(2)] IReadOnlyList<string> Capabilities);

[MessagePackObject]
public sealed record AuthPayload(
    [property: Key(0)] string Username,
    [property: Key(1)] string Secret,
    [property: Key(2)] bool IsAgentToken);

[MessagePackObject]
public sealed record AuthOkPayload(
    [property: Key(0)] string SessionId,
    [property: Key(1)] string Nick,
    [property: Key(2)] bool IsAgent);

[MessagePackObject]
public sealed record AuthFailPayload(
    [property: Key(0)] string Reason);

[MessagePackObject]
public sealed record PingPayload([property: Key(0)] long Timestamp);

[MessagePackObject]
public sealed record PongPayload([property: Key(0)] long Timestamp);

[MessagePackObject]
public sealed record ByePayload([property: Key(0)] string? Reason);

// ---- Presence & rooms ----

[MessagePackObject]
public sealed record NickPayload([property: Key(0)] string Nick);

/// <summary>Client→server: join a room (<see cref="Nick"/> ignored). Server→room: announce a
/// join, with <see cref="Nick"/> set to who joined.</summary>
[MessagePackObject]
public sealed record JoinPayload(
    [property: Key(0)] string Room,
    [property: Key(1)] string? Nick = null);

/// <summary>Client→server: leave a room. Server→room: announce a part, with
/// <see cref="Nick"/> set to who left.</summary>
[MessagePackObject]
public sealed record PartPayload(
    [property: Key(0)] string Room,
    [property: Key(1)] string? Reason,
    [property: Key(2)] string? Nick = null);

[MessagePackObject]
public sealed record RoomListPayload(
    [property: Key(0)] IReadOnlyList<RoomSummary> Rooms);

[MessagePackObject]
public sealed record RoomSummary(
    [property: Key(0)] string Name,
    [property: Key(1)] string? Topic,
    [property: Key(2)] int MemberCount);

[MessagePackObject]
public sealed record RoomMembersPayload(
    [property: Key(0)] string Room,
    [property: Key(1)] IReadOnlyList<MemberInfo> Members);

/// <summary>Modes are IRC-style single characters ("o" op, "v" voice), joined into a string.</summary>
[MessagePackObject]
public sealed record MemberInfo(
    [property: Key(0)] string Nick,
    [property: Key(1)] bool IsAgent,
    [property: Key(2)] string Modes);

[MessagePackObject]
public sealed record TopicPayload(
    [property: Key(0)] string Room,
    [property: Key(1)] string Topic);

[MessagePackObject]
public sealed record KickPayload(
    [property: Key(0)] string Room,
    [property: Key(1)] string Nick,
    [property: Key(2)] string? Reason);

[MessagePackObject]
public sealed record ModePayload(
    [property: Key(0)] string Room,
    [property: Key(1)] string Nick,
    [property: Key(2)] string Modes,
    [property: Key(3)] bool Grant);

[MessagePackObject]
public sealed record WhoisPayload([property: Key(0)] string Nick);

// ---- Chat ----

/// <summary>A room message. <see cref="FileId"/> optionally references a stored file (§5a)
/// which clients render inline. <see cref="Sender"/>, <see cref="Timestamp"/>, and
/// <see cref="MessageId"/> are authoritative from the server — values a client sends are
/// overwritten on relay.</summary>
[MessagePackObject]
public sealed record MsgPayload(
    [property: Key(0)] string Room,
    [property: Key(1)] string Sender,
    [property: Key(2)] string Text,
    [property: Key(3)] long Timestamp,
    [property: Key(4)] string? FileId,
    [property: Key(5)] string? MessageId = null);

[MessagePackObject]
public sealed record PrivMsgPayload(
    [property: Key(0)] string Sender,
    [property: Key(1)] string Recipient,
    [property: Key(2)] string Text,
    [property: Key(3)] long Timestamp);

[MessagePackObject]
public sealed record TypingPayload(
    [property: Key(0)] string Room,
    [property: Key(1)] string Nick);

/// <summary>Cursor-paged history request. <see cref="BeforeMessageId"/> null means "latest".</summary>
[MessagePackObject]
public sealed record HistoryReqPayload(
    [property: Key(0)] string Room,
    [property: Key(1)] string? BeforeMessageId,
    [property: Key(2)] int Limit);

[MessagePackObject]
public sealed record HistoryChunkPayload(
    [property: Key(0)] string Room,
    [property: Key(1)] IReadOnlyList<MsgPayload> Messages,
    [property: Key(2)] string? NextCursor);

// ---- Streaming ----

[MessagePackObject]
public sealed record MsgStreamStartPayload(
    [property: Key(0)] string Room,
    [property: Key(1)] string Sender,
    [property: Key(2)] string StreamId);

[MessagePackObject]
public sealed record MsgStreamDeltaPayload(
    [property: Key(0)] string StreamId,
    [property: Key(1)] string Delta);

/// <summary>Ends a stream. <see cref="FinalText"/> is authoritative — clients replace the
/// accumulated deltas with it so a dropped delta cannot corrupt the message.</summary>
[MessagePackObject]
public sealed record MsgStreamEndPayload(
    [property: Key(0)] string StreamId,
    [property: Key(1)] string FinalText,
    [property: Key(2)] long Timestamp);

// ---- Files (room-scoped storage, §5a) ----

/// <summary>Starts an upload. Server replies with <see cref="FileInfoPayload"/>: when
/// <c>Complete</c> is already true the content was deduplicated by hash and no chunks are
/// needed. <see cref="Quiet"/> suppresses the room announcement message.</summary>
[MessagePackObject]
public sealed record FilePutStartPayload(
    [property: Key(0)] string Room,
    [property: Key(1)] string Name,
    [property: Key(2)] string MimeType,
    [property: Key(3)] long Size,
    [property: Key(4)] string Sha256,
    [property: Key(5)] string? Description,
    [property: Key(6)] bool Quiet);

/// <summary>One sequential upload chunk; <see cref="Offset"/> must equal bytes received so far.</summary>
[MessagePackObject]
public sealed record FilePutChunkPayload(
    [property: Key(0)] string FileId,
    [property: Key(1)] long Offset,
    [property: Key(2)] byte[] Data);

[MessagePackObject]
public sealed record FilePutEndPayload([property: Key(0)] string FileId);

/// <summary>Requests up to <see cref="MaxBytes"/> from <see cref="Offset"/>; reply is a
/// <see cref="FileChunkPayload"/>. Loop until <c>Eof</c>.</summary>
[MessagePackObject]
public sealed record FileGetPayload(
    [property: Key(0)] string FileId,
    [property: Key(1)] long Offset,
    [property: Key(2)] int MaxBytes);

[MessagePackObject]
public sealed record FileChunkPayload(
    [property: Key(0)] string FileId,
    [property: Key(1)] long Offset,
    [property: Key(2)] byte[] Data,
    [property: Key(3)] bool Eof);

/// <summary>Request: room + empty list (like ROOM_LIST). Reply: the room's visible files.</summary>
[MessagePackObject]
public sealed record FileListPayload(
    [property: Key(0)] string Room,
    [property: Key(1)] IReadOnlyList<FileInfoPayload> Files);

/// <summary>File metadata. As a request, only <see cref="FileId"/> matters — use
/// <see cref="Request"/>.</summary>
[MessagePackObject]
public sealed record FileInfoPayload(
    [property: Key(0)] string FileId,
    [property: Key(1)] string Name,
    [property: Key(2)] string MimeType,
    [property: Key(3)] long Size,
    [property: Key(4)] string Sha256,
    [property: Key(5)] string Uploader,
    [property: Key(6)] long CreatedAt,
    [property: Key(7)] string? Description,
    [property: Key(8)] IReadOnlyList<string> Rooms,
    [property: Key(9)] bool Complete)
{
    public static FileInfoPayload Request(string fileId) => new(fileId, "", "", 0, "", "", 0, null, [], false);
}

[MessagePackObject]
public sealed record FileGrantPayload(
    [property: Key(0)] string FileId,
    [property: Key(1)] string Room);

[MessagePackObject]
public sealed record FileRevokePayload(
    [property: Key(0)] string FileId,
    [property: Key(1)] string Room);

[MessagePackObject]
public sealed record FileDeletePayload([property: Key(0)] string FileId);

// ---- Generic ----

[MessagePackObject]
public sealed record ErrorPayload(
    [property: Key(0)] string Code,
    [property: Key(1)] string Message);

[MessagePackObject]
public sealed record OkPayload;
