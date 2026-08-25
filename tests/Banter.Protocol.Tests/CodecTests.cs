using Banter.Protocol;
using Xunit;

namespace Banter.Protocol.Tests;

public sealed class CodecTests
{
    public static TheoryData<object> AllPayloads() => new()
    {
        new HelloPayload("Banter.Cli", "0.1.0", ["banter.core"]),
        new HelloPayload("Banter.Cli", "0.1.0", ["banter.core"], [new CapabilityRangePayload("banter.core", 1, 1)]),
        new AuthPayload("alice", "s3cret", IsAgentToken: false),
        new AuthOkPayload("session-1", "alice", IsAgent: false),
        new AuthFailPayload("bad credentials"),
        new PingPayload(1234567890),
        new PongPayload(1234567890),
        new ByePayload("leaving"),
        new NickPayload("alice"),
        new JoinPayload("#main"),
        new PartPayload("#main", null),
        new RoomListPayload([new RoomSummary("#main", "the main channel", 3)]),
        new RoomMembersPayload("#main", [new MemberInfo("alice", false, "o"), new MemberInfo("dagger", true, "")]),
        new TopicPayload("#main", "welcome"),
        new KickPayload("#main", "mallory", "spam"),
        new ModePayload("#main", "dagger", "v", Grant: true),
        new WhoisPayload("dagger"),
        new MsgPayload("#main", "alice", "hello agents", 1234567890, null),
        new PrivMsgPayload("alice", "bob", "psst", 1234567890),
        new TypingPayload("#main", "alice"),
        new HistoryReqPayload("#main", null, 50),
        new HistoryChunkPayload("#main", [new MsgPayload("#main", "alice", "hi", 1, null)], "cursor-2"),
        new FilePutStartPayload("#main", "cat.png", "image/png", 4, "abc123", "a cat", Quiet: false),
        new FilePutChunkPayload("file-1", 0, [1, 2, 3, 4]),
        new FilePutEndPayload("file-1"),
        new FileGetPayload("file-1", 0, 65536),
        new FileChunkPayload("file-1", 0, [1, 2, 3, 4], Eof: true),
        new FileListPayload("#main", [FileInfoPayload.Request("file-1")]),
        new FileInfoPayload("file-1", "cat.png", "image/png", 4, "abc123", "alice", 1234567890, null, ["#main"], true),
        new FileGrantPayload("file-1", "#other"),
        new FileRevokePayload("file-1", "#other"),
        new FileDeletePayload("file-1"),
        new MsgStreamStartPayload("#main", "dagger", "stream-1"),
        new MsgStreamDeltaPayload("stream-1", "tok"),
        new MsgStreamEndPayload("stream-1", "tokens joined", 1234567890),
        new ErrorPayload("NO_SUCH_ROOM", "#nope does not exist"),
        new OkPayload(),
    };

    [Theory]
    [MemberData(nameof(AllPayloads))]
    public void EveryPayloadRoundTripsThroughMessagePack(object payload) =>
        AssertRoundTrip(new BanterCodec(BanterWireFormat.MessagePack), payload);

    [Theory]
    [MemberData(nameof(AllPayloads))]
    public void EveryPayloadRoundTripsThroughJsonDebugMode(object payload) =>
        AssertRoundTrip(new BanterCodec(BanterWireFormat.Json), payload);

    [Fact]
    public void EnvelopeCarriesVersionTypeAndCorrelation()
    {
        var codec = new BanterCodec();
        var request = codec.CreateEnvelope(new JoinPayload("#main"));
        var response = codec.CreateEnvelope(new OkPayload(), replyTo: request.MsgId);

        Assert.Equal(BanterEnvelope.CurrentVersion, request.Ver);
        Assert.Equal(BanterMessageType.Join, request.Type);
        Assert.Null(request.ReplyTo);
        Assert.Equal(request.MsgId, response.ReplyTo);
        Assert.NotEqual(request.MsgId, response.MsgId);
    }

    [Fact]
    public void UnknownMessageTypeDecodesEnvelopeButNotPayload()
    {
        var codec = new BanterCodec();
        var envelope = new BanterEnvelope(
            BanterEnvelope.CurrentVersion,
            (BanterMessageType)9999,
            BanterEnvelope.NewMsgId(),
            null,
            [1, 2, 3]);

        var decoded = codec.DecodeEnvelope(codec.EncodeEnvelope(envelope));

        Assert.Equal((BanterMessageType)9999, decoded.Type);
        Assert.Null(codec.DecodePayload(decoded));
    }

    [Fact]
    public void ReservedMessageTypesAreLegalWithoutContracts()
    {
        // Agent pause/resume and MCP grants are still enum-reserved without contracts; the
        // agent-control and work-ledger types gained theirs with §8a and §8b.
        Assert.Null(PayloadRegistry.PayloadTypeFor(BanterMessageType.AgentPause));
        Assert.Null(PayloadRegistry.PayloadTypeFor(BanterMessageType.AgentResume));
        Assert.Null(PayloadRegistry.PayloadTypeFor(BanterMessageType.AgentMcpGrants));
    }

    [Fact]
    public void AgentControlTypesUsedByDelegationHaveContracts()
    {
        Assert.Equal(typeof(AgentAnnouncePayload), PayloadRegistry.PayloadTypeFor(BanterMessageType.AgentAnnounce));
        Assert.Equal(typeof(AgentListPayload), PayloadRegistry.PayloadTypeFor(BanterMessageType.AgentList));
        Assert.Equal(typeof(AgentMovePayload), PayloadRegistry.PayloadTypeFor(BanterMessageType.AgentMove));
        Assert.Equal(typeof(RoomCreatePayload), PayloadRegistry.PayloadTypeFor(BanterMessageType.RoomCreate));
        Assert.Equal(typeof(RoomDelegatorPayload), PayloadRegistry.PayloadTypeFor(BanterMessageType.RoomDelegator));
        Assert.Equal(typeof(RoomModePayload), PayloadRegistry.PayloadTypeFor(BanterMessageType.RoomMode));
    }

    [Fact]
    public void RegistryIsBidirectionallyConsistent()
    {
        foreach (var messageType in PayloadRegistry.RegisteredMessageTypes)
        {
            var payloadType = PayloadRegistry.PayloadTypeFor(messageType);
            Assert.NotNull(payloadType);
            Assert.Equal(messageType, PayloadRegistry.MessageTypeFor(payloadType!));
        }
    }

    private static void AssertRoundTrip(BanterCodec codec, object payload)
    {
        var envelope = codec.CreateEnvelope(payload);
        var wire = codec.EncodeEnvelope(envelope);
        var decodedEnvelope = codec.DecodeEnvelope(wire);
        var decodedPayload = codec.DecodePayload(decodedEnvelope);

        Assert.Equal(envelope.Type, decodedEnvelope.Type);
        Assert.Equal(envelope.MsgId, decodedEnvelope.MsgId);
        Assert.NotNull(decodedPayload);
        Assert.Equal(payload.GetType(), decodedPayload.GetType());
        // Record equality is reference-based for list-typed properties, so compare the
        // canonical serialized form instead — equal bytes means equal wire data.
        Assert.Equal(envelope.Payload, codec.CreateEnvelope(decodedPayload).Payload);
    }
}
