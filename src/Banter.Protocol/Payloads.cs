using MessagePack;

namespace Banter.Protocol;

// v1 payload contracts for the session, presence/rooms, chat, and streaming areas.
// File, agent-control, and task payloads land with their phases; their message types and
// number ranges are already reserved in BanterMessageType.

// ---- Session ----

/// <summary>First message on a channel, both directions, carrying the CupriMark negotiation
/// payload: per-component supported ordinal ranges (only ordinals travel; meanings resolve
/// against each side's own catalogue). <see cref="Capabilities"/> remains a free-form hint list.</summary>
[MessagePackObject]
public sealed record HelloPayload(
    [property: Key(0)] string ClientName,
    [property: Key(1)] string ClientVersion,
    [property: Key(2)] IReadOnlyList<string> Capabilities,
    [property: Key(3)] IReadOnlyList<CapabilityRangePayload>? Ranges = null);

/// <summary>A CupriMark supported-ordinal range for one catalogue component.</summary>
[MessagePackObject]
public sealed record CapabilityRangePayload(
    [property: Key(0)] string Component,
    [property: Key(1)] ushort Low,
    [property: Key(2)] ushort High);

[MessagePackObject]
public sealed record AuthPayload(
    [property: Key(0)] string Username,
    [property: Key(1)] string Secret,
    [property: Key(2)] bool IsAgentToken);

[MessagePackObject]
public sealed record AuthOkPayload(
    [property: Key(0)] string SessionId,
    [property: Key(1)] string Nick,
    [property: Key(2)] bool IsAgent,

    // A trailing optional field, so an older peer that never sends it simply reads as "not an
    // admin" rather than failing to decode. The client needs this to decide whether to offer
    // operator UI at all — a button that always ends in NOT_ADMIN is worse than no button.
    [property: Key(3)] bool IsAdmin = false);

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

/// <summary>
/// One room in a listing. <c>ParentRoom</c> is set for a sub-room, so a client can show the room
/// list as a hierarchy rather than a flat set of names with no relationship between them.
/// Additive key: an older peer simply does not send it.
/// </summary>
[MessagePackObject]
public sealed record RoomSummary(
    [property: Key(0)] string Name,
    [property: Key(1)] string? Topic,
    [property: Key(2)] int MemberCount,
    [property: Key(3)] string? ParentRoom = null);

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
    [property: Key(5)] string? MessageId = null,
    // Carried so replayed history looks like the room did. Without them a reconnect loses every
    // "edited" marker, and a message someone took back comes back as a blank line.
    [property: Key(6)] long EditedAt = 0,
    [property: Key(7)] long DeletedAt = 0);

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

/// <summary>
/// Change what a message says. The client sends room, id and the new text; the server fills
/// <c>Sender</c> and <c>EditedAt</c> on the copy it broadcasts.
///
/// <para>Only the author may edit. An operator rewriting somebody else's line would be putting
/// words in their mouth under their name, which is worse than having no edit at all — moderation
/// is what <see cref="DeletePayload"/> is for.</para>
/// </summary>
[MessagePackObject]
public sealed record EditPayload(
    [property: Key(0)] string Room,
    [property: Key(1)] string MessageId,
    [property: Key(2)] string Text,
    [property: Key(3)] string? Sender = null,
    [property: Key(4)] long EditedAt = 0);

/// <summary>
/// Take a message back. The author may delete their own; an admin may delete anyone's, which is
/// the moderation path.
///
/// <para>The text is removed from storage rather than hidden — "delete" that only stops rendering
/// is a lie to whoever asked for it. What remains is the fact of the deletion, because clients
/// have already drawn the message and history pages are cursored by message id: a row that simply
/// vanished would strand any client paging through it.</para>
/// </summary>
[MessagePackObject]
public sealed record DeletePayload(
    [property: Key(0)] string Room,
    [property: Key(1)] string MessageId,
    [property: Key(2)] string? Sender = null,
    [property: Key(3)] long DeletedAt = 0);

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
/// accumulated deltas with it so a dropped delta cannot corrupt the message. The server stamps
/// <see cref="MessageId"/> on the relayed END; it matches the message persisted to history, so
/// clients reconcile a streamed message with backscroll.</summary>
[MessagePackObject]
public sealed record MsgStreamEndPayload(
    [property: Key(0)] string StreamId,
    [property: Key(1)] string FinalText,
    [property: Key(2)] long Timestamp,
    [property: Key(3)] string? MessageId = null);

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

// ---- Agent control (PLAN §8a) ----

/// <summary>
/// Where an agent runs, which is really "does data leave the building". The single most
/// important routing attribute: a <see cref="Frontier"/> agent is a third party, so anything
/// sent to it has left our control the moment the request is made.
/// </summary>
public enum AgentLocality
{
    /// <summary>Unstated. Treated as <see cref="Frontier"/> when routing, because assuming an
    /// unknown agent is local is the mistake that leaks data.</summary>
    Unknown = 0,

    /// <summary>Runs on hardware we control (a local model, an on-prem endpoint).</summary>
    Local = 1,

    /// <summary>A third-party hosted model — Claude, Codex, Copilot.</summary>
    Frontier = 2,
}

/// <summary>The most sensitive data an agent may be given.</summary>
public enum DataSensitivity
{
    /// <summary>Unstated. Routing treats this as <see cref="Sensitive"/> — fail closed.</summary>
    Unknown = 0,
    Public = 1,
    Internal = 2,
    Sensitive = 3,
}

/// <summary>How a room dispatches human messages to agents.</summary>
public enum RoomDispatchMode
{
    /// <summary>Default: one elected delegator acts and routes to suitable agents.</summary>
    Delegated = 0,

    /// <summary>Every agent answers when its own nick is mentioned.</summary>
    Mention = 1,
}

/// <summary>
/// Agent → server on join: what this agent is and what it may be trusted with. The delegator
/// routes on exactly these fields (PLAN §8a).
/// </summary>
[MessagePackObject]
public sealed record AgentAnnouncePayload(
    [property: Key(0)] string Nick,
    [property: Key(1)] AgentLocality Locality,
    [property: Key(2)] DataSensitivity Clearance,
    [property: Key(3)] IReadOnlyList<string> Skills,
    [property: Key(4)] string Description = "",
    [property: Key(5)] int CostTier = 1,
    [property: Key(6)] bool WantsDelegator = false);

/// <summary>One roster entry, as the server holds it.</summary>
[MessagePackObject]
public sealed record AgentInfoPayload(
    [property: Key(0)] string Nick,
    [property: Key(1)] AgentLocality Locality,
    [property: Key(2)] DataSensitivity Clearance,
    [property: Key(3)] IReadOnlyList<string> Skills,
    [property: Key(4)] string Description,
    [property: Key(5)] int CostTier,
    [property: Key(6)] bool IsDelegator);

/// <summary>Server → client: the agents in a room and their attributes.</summary>
[MessagePackObject]
public sealed record AgentListPayload(
    [property: Key(0)] string Room,
    [property: Key(1)] IReadOnlyList<AgentInfoPayload> Agents);

/// <summary>
/// Server → room: who the delegator is. <see cref="Nick"/> is null when a room has no agents to
/// elect from. <see cref="Reason"/> records why this agent won, so the election is auditable
/// from the timeline rather than being an invisible server decision.
/// </summary>
[MessagePackObject]
public sealed record RoomDelegatorPayload(
    [property: Key(0)] string Room,
    [property: Key(1)] string? Nick,
    [property: Key(2)] string Reason = "");

/// <summary>
/// Open a room. When <see cref="ParentRoom"/> is set the new room is a <b>sub-room</b>: the
/// delegator's side channel for a piece of work (PLAN §8a).
///
/// <para>A sub-room <b>inherits its parent's sensitivity</b>. Without that, opening a child room
/// would be a way to launder sensitive context into a room where a frontier agent is eligible —
/// the sub-room must be no more permissive than the conversation it came from.</para>
/// </summary>
[MessagePackObject]
public sealed record RoomCreatePayload(
    [property: Key(0)] string Room,
    [property: Key(1)] string? ParentRoom = null,
    [property: Key(2)] string Purpose = "");

/// <summary>
/// Move an agent into a room. Issued by a room's delegator to pull a chosen agent into a
/// sub-room, or by an op. Refused when the agent is not cleared for that room's sensitivity.
/// </summary>
[MessagePackObject]
public sealed record AgentMovePayload(
    [property: Key(0)] string Nick,
    [property: Key(1)] string Room,
    [property: Key(2)] string Reason = "");

/// <summary>Get (Mode omitted by convention) or set a room's dispatch mode.</summary>
[MessagePackObject]
public sealed record RoomModePayload(
    [property: Key(0)] string Room,
    [property: Key(1)] RoomDispatchMode Mode);

// ---- Work ledger (PLAN §8b) ----

/// <summary>Lifecycle of a unit of work. Terminal states are Done, Failed.</summary>
public enum TaskState
{
    Open = 0,
    Claimed = 1,
    Assigned = 2,
    Done = 3,
    Failed = 4,
}

/// <summary>
/// A task as the server holds it. <c>LeaseExpiresAt</c> is when the current claim lapses — the
/// server releases the task at that point, so a crashed agent cannot sit on work forever.
/// </summary>
[MessagePackObject]
public sealed record TaskInfoPayload(
    [property: Key(0)] string TaskId,
    [property: Key(1)] string Room,
    [property: Key(2)] string Title,
    [property: Key(3)] string Body,
    [property: Key(4)] string Poster,
    [property: Key(5)] TaskState State,
    [property: Key(6)] string? Assignee,
    [property: Key(7)] long CreatedAt,
    [property: Key(8)] long? ClaimedAt,
    [property: Key(9)] long? FinishedAt,
    [property: Key(10)] long? LeaseExpiresAt,
    [property: Key(11)] string? Result);

/// <summary>
/// Post work into a room. <see cref="TaskId"/> is empty on the request and filled by the server.
/// </summary>
[MessagePackObject]
public sealed record TaskPostPayload(
    [property: Key(0)] string Room,
    [property: Key(1)] string Title,
    [property: Key(2)] string Body = "",
    [property: Key(3)] int LeaseSeconds = 0,
    [property: Key(4)] string TaskId = "");

/// <summary>Claim an open task. First claim wins; losers get a clean refusal.</summary>
[MessagePackObject]
public sealed record TaskClaimPayload([property: Key(0)] string TaskId);

/// <summary>Assign a task to an agent. Delegator- or op-only.</summary>
[MessagePackObject]
public sealed record TaskAssignPayload(
    [property: Key(0)] string TaskId,
    [property: Key(1)] string Nick);

/// <summary>Give a task back to the pool. Sent by the holder, or by the server on lease expiry.</summary>
[MessagePackObject]
public sealed record TaskReleasePayload(
    [property: Key(0)] string TaskId,
    [property: Key(1)] string Reason = "");

/// <summary>Progress note. Also renews the lease, which is how a long job stays held.</summary>
[MessagePackObject]
public sealed record TaskUpdatePayload(
    [property: Key(0)] string TaskId,
    [property: Key(1)] string Note);

/// <summary>Finish a task, successfully or not.</summary>
[MessagePackObject]
public sealed record TaskDonePayload(
    [property: Key(0)] string TaskId,
    [property: Key(1)] string Result = "",
    [property: Key(2)] bool Success = true);

/// <summary>
/// Query tasks in a room; the reply carries them. <c>IncludeFinished</c> is a request filter —
/// set it to see terminal tasks as well as live ones.
/// </summary>
[MessagePackObject]
public sealed record TaskListPayload(
    [property: Key(0)] string Room,
    [property: Key(1)] IReadOnlyList<TaskInfoPayload> Tasks,
    [property: Key(2)] bool IncludeFinished = false);

// ---- Agent tools (PLAN §8: MCP access) ----

/// <summary>
/// One tool an agent may call. <c>Schema</c> is the raw JSON Schema for its arguments, passed
/// through to the model unchanged — the server does not reinterpret it.
/// </summary>
[MessagePackObject]
public sealed record ToolDescriptorPayload(
    [property: Key(0)] string Name,
    [property: Key(1)] string Description,
    [property: Key(2)] string Schema,
    [property: Key(3)] string ServerKey);

/// <summary>
/// Agent → server: the tools this agent is granted. Sent empty; the reply carries the set.
///
/// <para>An ungranted tool is absent from the list rather than merely refused on call, so an
/// agent cannot discover what it is not allowed to use.</para>
/// </summary>
[MessagePackObject]
public sealed record ToolListPayload(
    [property: Key(0)] IReadOnlyList<ToolDescriptorPayload> Tools);

/// <summary>
/// Agent → server: run a tool. <c>Arguments</c> is JSON as the model produced it.
///
/// <para>Tools run on the server because that is where the credentials are: an agent holding an
/// API token could use it outside anything Banter can see or audit.</para>
/// </summary>
[MessagePackObject]
public sealed record ToolCallPayload(
    [property: Key(0)] string Name,
    [property: Key(1)] string Arguments,
    [property: Key(2)] string Room = "");

/// <summary>Server → agent: what the tool returned, or why it did not run.</summary>
[MessagePackObject]
public sealed record ToolResultPayload(
    [property: Key(0)] string Name,
    [property: Key(1)] string Content,
    [property: Key(2)] bool IsError);

/// <summary>
/// Operator: which tools an agent may use. Sent with <c>Tools</c> empty to read the current set,
/// or populated to replace it. <c>Agent</c> empty means the default grant for agents with none.
/// </summary>
[MessagePackObject]
public sealed record ToolGrantsPayload(
    [property: Key(0)] string Agent,
    [property: Key(1)] IReadOnlyList<string> Tools,
    [property: Key(2)] bool Replace = false);

// ---- Agent identities (PLAN §8a) ----

/// <summary>
/// One agent identity as an operator sees it. There is no secret here and never will be: the
/// agent's private key is made on its own machine and never sent, so this is the whole of what
/// the server knows about who an agent is.
///
/// <para><c>Locality</c> is "local" or "frontier" — the axis deciding whether data may leave —
/// and <c>Clearance</c> is "public", "internal" or "sensitive". <c>KeyFingerprint</c> tells one
/// machine from another and is empty until a machine has enrolled.</para>
/// </summary>
[MessagePackObject]
public sealed record AgentIdentityPayload(
    [property: Key(0)] string Nick,
    [property: Key(1)] IReadOnlyList<string> Rooms,
    [property: Key(2)] IReadOnlyList<string> Skills,
    [property: Key(3)] string Locality,
    [property: Key(4)] string Clearance,
    [property: Key(5)] bool Enrolled,
    [property: Key(6)] string KeyFingerprint,
    [property: Key(7)] bool EnrolmentPending);

/// <summary>Admin → server: create an identity. Everything but the nick may be changed later.</summary>
[MessagePackObject]
public sealed record AgentIdentityCreatePayload(
    [property: Key(0)] string Nick,
    [property: Key(1)] IReadOnlyList<string> Rooms,
    [property: Key(2)] IReadOnlyList<string> Skills,
    [property: Key(3)] string Locality,
    [property: Key(4)] string Clearance);

/// <summary>
/// Server → admin: the identity, and the one-time code to paste into the machine that will run it.
///
/// <para>This is the only message that ever carries the code, and it is only ever sent to the
/// admin who asked. It is single-use and short-lived: it buys the machine the right to register a
/// key once, and is spent the moment it does.</para>
/// </summary>
[MessagePackObject]
public sealed record AgentEnrolmentCodePayload(
    [property: Key(0)] string Nick,
    [property: Key(1)] string Code,
    [property: Key(2)] long ExpiresAtUnix);

/// <summary>Admin → server: change an identity. Null fields are left as they are.</summary>
[MessagePackObject]
public sealed record AgentIdentityUpdatePayload(
    [property: Key(0)] string Nick,
    [property: Key(1)] IReadOnlyList<string>? Rooms = null,
    [property: Key(2)] IReadOnlyList<string>? Skills = null,
    [property: Key(3)] string? Locality = null,
    [property: Key(4)] string? Clearance = null);

/// <summary>Admin → server: remove an identity. Any session holding its key is dropped.</summary>
[MessagePackObject]
public sealed record AgentIdentityDeletePayload([property: Key(0)] string Nick);

/// <summary>Admin → server: a fresh code, for a machine being replaced. Revokes the enrolled key.</summary>
[MessagePackObject]
public sealed record AgentIdentityReissuePayload([property: Key(0)] string Nick);

/// <summary>Admin → server: list the identities.</summary>
[MessagePackObject]
public sealed record AgentIdentityListPayload;

/// <summary>Server → admin: the identities.</summary>
[MessagePackObject]
public sealed record AgentIdentitiesPayload(
    [property: Key(0)] IReadOnlyList<AgentIdentityPayload> Identities);

/// <summary>
/// Agent → server: redeem an enrolment code by registering the public half of a key just generated
/// on this machine. The private half never leaves it, and is not in this message.
/// <c>PublicKey</c> is SubjectPublicKeyInfo for a P-256 ECDSA key.
/// </summary>
[MessagePackObject]
public sealed record AgentEnrolPayload(
    [property: Key(0)] string Code,
    [property: Key(1)] byte[] PublicKey);

/// <summary>
/// One user account, as the users page sees it. No credential material of any kind: the server
/// stores only a hash, and even the admin never learns a password that is in use.
/// </summary>
[MessagePackObject]
public sealed record UserAccountPayload(
    [property: Key(0)] string Username,
    [property: Key(1)] bool IsAdmin);

/// <summary>Admin → server: create a user. The server invents the temporary password.</summary>
[MessagePackObject]
public sealed record UserCreatePayload(
    [property: Key(0)] string Username,
    [property: Key(1)] bool IsAdmin);

/// <summary>
/// Server → admin: the temporary password, in reply to a create or a reset.
///
/// <para>The only message that ever carries a user's password, and it is only ever sent to the
/// admin who asked, for a credential the server just invented. It is meant to be spoken or pasted
/// to the person once and then changed by them; a lost one is reset, not looked up.</para>
/// </summary>
[MessagePackObject]
public sealed record UserTempPasswordPayload(
    [property: Key(0)] string Username,
    [property: Key(1)] string Password);

/// <summary>Admin → server: change an account. Null fields are left as they are.</summary>
[MessagePackObject]
public sealed record UserUpdatePayload(
    [property: Key(0)] string Username,
    [property: Key(1)] bool? IsAdmin = null);

/// <summary>
/// Admin → server: remove an account. The credential dies immediately; a session already signed
/// in lives until it disconnects, the same bargain the agents page strikes on delete.
/// </summary>
[MessagePackObject]
public sealed record UserDeletePayload([property: Key(0)] string Username);

/// <summary>Admin → server: list the user accounts.</summary>
[MessagePackObject]
public sealed record UserListPayload;

/// <summary>Server → admin: the user accounts.</summary>
[MessagePackObject]
public sealed record UsersPayload(
    [property: Key(0)] IReadOnlyList<UserAccountPayload> Users);

/// <summary>Admin → server: a fresh temporary password. The old password stops working.</summary>
[MessagePackObject]
public sealed record UserPasswordResetPayload([property: Key(0)] string Username);

/// <summary>
/// Signed-in human → server: change their own password. The old one is required even though the
/// session is already authenticated — an unattended signed-in machine must not be enough to
/// lock the real owner out.
/// </summary>
[MessagePackObject]
public sealed record PasswordChangePayload(
    [property: Key(0)] string OldPassword,
    [property: Key(1)] string NewPassword);

/// <summary>Agent → server: I am this nick and I hold its key — send me something to sign.</summary>
[MessagePackObject]
public sealed record AuthChallengePayload([property: Key(0)] string Username);

/// <summary>
/// Server → agent: sign this. A fresh random nonce per attempt, so a captured signature proves
/// nothing the second time.
/// </summary>
[MessagePackObject]
public sealed record AuthChallengeIssuedPayload([property: Key(0)] byte[] Nonce);

/// <summary>Agent → server: the challenge, signed.</summary>
[MessagePackObject]
public sealed record AuthKeyPayload(
    [property: Key(0)] string Username,
    [property: Key(1)] byte[] Signature);

// ---- Generic ----

[MessagePackObject]
public sealed record ErrorPayload(
    [property: Key(0)] string Code,
    [property: Key(1)] string Message);

[MessagePackObject]
public sealed record OkPayload;
