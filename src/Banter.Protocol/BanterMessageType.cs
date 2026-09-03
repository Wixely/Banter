namespace Banter.Protocol;

/// <summary>
/// Wire identifiers for BanterProtocol v1 messages. Ranges are reserved per protocol area
/// (session, presence/rooms, chat, streaming, files, agent control, work ledger) so areas can
/// grow without renumbering. Values are part of the wire contract — never reuse or renumber.
/// </summary>
public enum BanterMessageType : ushort
{
    // Session (1–9)
    Hello = 1,
    Auth = 2,
    AuthOk = 3,
    AuthFail = 4,
    Ping = 5,
    Pong = 6,
    Bye = 7,

    // Presence & rooms, IRC-shaped (10–19)
    Nick = 10,
    Join = 11,
    Part = 12,
    RoomList = 13,
    RoomMembers = 14,
    Topic = 15,
    Kick = 16,
    Mode = 17,
    Whois = 18,

    /// <summary>Open a room, optionally as a child of another (PLAN §8a sub-rooms).</summary>
    RoomCreate = 19,

    // Chat (20–29)
    Msg = 20,
    PrivMsg = 21,
    Typing = 22,
    HistoryReq = 23,
    HistoryChunk = 24,
    Edit = 25,      // schema reserved, nice-to-have
    Delete = 26,    // schema reserved, nice-to-have

    // Streaming (30–39)
    MsgStreamStart = 30,
    MsgStreamDelta = 31,
    MsgStreamEnd = 32,

    // Files, room-scoped storage (40–59) — payloads land in Phase 2
    FilePutStart = 40,
    FilePutChunk = 41,
    FilePutEnd = 42,
    FileGet = 43,
    FileChunk = 44,
    FileList = 45,
    FileInfo = 46,
    FileGrant = 47,
    FileRevoke = 48,
    FileDelete = 49,

    // Agent control, server-op only (60–69) — payloads land in Phase 5
    AgentList = 60,
    AgentMove = 61,
    AgentPause = 62,
    AgentResume = 63,
    AgentStatus = 64,
    AgentMcpGrants = 65,

    /// <summary>Agent → server on join: the attributes the delegator routes on (PLAN §8a).</summary>
    AgentAnnounce = 66,

    /// <summary>Server → room: who the delegator is, or none.</summary>
    RoomDelegator = 67,

    /// <summary>Get or set a room's dispatch mode (delegated / mention).</summary>
    RoomMode = 68,

    // Work ledger (70–79) — payloads land in Phase 5
    TaskPost = 70,
    TaskClaim = 71,
    TaskAssign = 72,
    TaskRelease = 73,
    TaskUpdate = 74,
    TaskDone = 75,
    TaskFail = 76,
    TaskList = 77,

    // Agent tools, server-side execution (80–89)
    /// <summary>Agent → server: which tools may I use? Server answers with the granted set.</summary>
    ToolList = 80,

    /// <summary>Agent → server: run this tool. The server holds the credentials, not the agent.</summary>
    ToolCall = 81,

    /// <summary>Server → agent: the result of a tool call.</summary>
    ToolResult = 82,

    /// <summary>Operator: read or change which tools an agent may use.</summary>
    ToolGrants = 83,


    // Agent identities: who an agent is, and how a new one is let in (90-109)
    /// <summary>Admin: create an agent identity. The reply carries its one-time enrolment code.</summary>
    AgentIdentityCreate = 90,

    /// <summary>Admin: change an existing identity's rooms, skills, locality or clearance.</summary>
    AgentIdentityUpdate = 91,

    /// <summary>Admin: remove an identity. Its key stops working immediately.</summary>
    AgentIdentityDelete = 92,

    /// <summary>Admin: list the agent identities this server knows.</summary>
    AgentIdentityList = 93,

    /// <summary>Admin: the identities, in reply to a list.</summary>
    AgentIdentities = 94,

    /// <summary>Admin: mint a fresh enrolment code for an identity whose machine is being replaced.</summary>
    AgentIdentityReissue = 95,

    /// <summary>Agent: redeem an enrolment code, registering the public half of a key it just made.</summary>
    AgentEnrol = 96,

    /// <summary>Agent: ask for a challenge to sign, instead of presenting a password.</summary>
    AuthChallenge = 97,

    /// <summary>Server: the nonce to sign.</summary>
    AuthChallengeIssued = 98,

    /// <summary>Agent: the signed challenge.</summary>
    AuthKey = 99,

    /// <summary>Server → admin only: the one-time code, in reply to a create or reissue.</summary>
    AgentEnrolmentCode = 100,

    /// <summary>Server → agent: the identity it just enrolled as.</summary>
    AgentIdentityInfo = 101,


    // Generic (250–255)
    Error = 250,
    Ok = 251,
}
