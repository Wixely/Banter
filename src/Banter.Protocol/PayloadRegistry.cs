namespace Banter.Protocol;

/// <summary>
/// Maps <see cref="BanterMessageType"/> values to their CLR payload types, both directions.
/// Message types without an entry are legal on the wire (they may belong to a newer peer or an
/// unimplemented area) — decoding them yields the raw envelope with no typed payload.
/// </summary>
public static class PayloadRegistry
{
    private static readonly Dictionary<BanterMessageType, Type> ByMessage = new()
    {
        [BanterMessageType.Hello] = typeof(HelloPayload),
        [BanterMessageType.Auth] = typeof(AuthPayload),
        [BanterMessageType.AuthOk] = typeof(AuthOkPayload),
        [BanterMessageType.AuthFail] = typeof(AuthFailPayload),
        [BanterMessageType.Ping] = typeof(PingPayload),
        [BanterMessageType.Pong] = typeof(PongPayload),
        [BanterMessageType.Bye] = typeof(ByePayload),
        [BanterMessageType.Nick] = typeof(NickPayload),
        [BanterMessageType.Join] = typeof(JoinPayload),
        [BanterMessageType.Part] = typeof(PartPayload),
        [BanterMessageType.RoomList] = typeof(RoomListPayload),
        [BanterMessageType.RoomMembers] = typeof(RoomMembersPayload),
        [BanterMessageType.Topic] = typeof(TopicPayload),
        [BanterMessageType.Kick] = typeof(KickPayload),
        [BanterMessageType.Mode] = typeof(ModePayload),
        [BanterMessageType.Whois] = typeof(WhoisPayload),
        [BanterMessageType.Msg] = typeof(MsgPayload),
        [BanterMessageType.PrivMsg] = typeof(PrivMsgPayload),
        [BanterMessageType.Typing] = typeof(TypingPayload),
        [BanterMessageType.HistoryReq] = typeof(HistoryReqPayload),
        [BanterMessageType.HistoryChunk] = typeof(HistoryChunkPayload),
        [BanterMessageType.FilePutStart] = typeof(FilePutStartPayload),
        [BanterMessageType.FilePutChunk] = typeof(FilePutChunkPayload),
        [BanterMessageType.FilePutEnd] = typeof(FilePutEndPayload),
        [BanterMessageType.FileGet] = typeof(FileGetPayload),
        [BanterMessageType.FileChunk] = typeof(FileChunkPayload),
        [BanterMessageType.FileList] = typeof(FileListPayload),
        [BanterMessageType.FileInfo] = typeof(FileInfoPayload),
        [BanterMessageType.FileGrant] = typeof(FileGrantPayload),
        [BanterMessageType.FileRevoke] = typeof(FileRevokePayload),
        [BanterMessageType.FileDelete] = typeof(FileDeletePayload),
        [BanterMessageType.MsgStreamStart] = typeof(MsgStreamStartPayload),
        [BanterMessageType.MsgStreamDelta] = typeof(MsgStreamDeltaPayload),
        [BanterMessageType.MsgStreamEnd] = typeof(MsgStreamEndPayload),
        [BanterMessageType.AgentAnnounce] = typeof(AgentAnnouncePayload),
        [BanterMessageType.AgentList] = typeof(AgentListPayload),
        [BanterMessageType.RoomDelegator] = typeof(RoomDelegatorPayload),
        [BanterMessageType.RoomMode] = typeof(RoomModePayload),
        [BanterMessageType.Error] = typeof(ErrorPayload),
        [BanterMessageType.Ok] = typeof(OkPayload),
    };

    private static readonly Dictionary<Type, BanterMessageType> ByType =
        ByMessage.ToDictionary(pair => pair.Value, pair => pair.Key);

    public static Type? PayloadTypeFor(BanterMessageType type) =>
        ByMessage.TryGetValue(type, out var payloadType) ? payloadType : null;

    public static BanterMessageType MessageTypeFor(Type payloadType) =>
        ByType.TryGetValue(payloadType, out var messageType)
            ? messageType
            : throw new ArgumentException($"{payloadType.Name} is not a registered BanterProtocol payload.", nameof(payloadType));

    public static IReadOnlyCollection<BanterMessageType> RegisteredMessageTypes => ByMessage.Keys;
}
