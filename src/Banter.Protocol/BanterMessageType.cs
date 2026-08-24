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

    // Work ledger (70–79) — payloads land in Phase 5
    TaskPost = 70,
    TaskClaim = 71,
    TaskAssign = 72,
    TaskRelease = 73,
    TaskUpdate = 74,
    TaskDone = 75,
    TaskFail = 76,
    TaskList = 77,

    // Generic (250–255)
    Error = 250,
    Ok = 251,
}
