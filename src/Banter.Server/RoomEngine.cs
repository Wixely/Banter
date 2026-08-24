using System.Threading.Channels;
using Banter.Core;
using Banter.Protocol;

namespace Banter.Server;

/// <summary>
/// The authoritative room state machine. All mutations flow through one command channel with a
/// single consumer, so ordering is deterministic and state needs no locks (PLAN §5). Fan-out is
/// non-blocking: sessions own outbound queues.
/// </summary>
internal sealed class RoomEngine
{
    private const int HistoryCapPerRoom = 1_000;

    private readonly Channel<Action> _commands = Channel.CreateUnbounded<Action>(
        new UnboundedChannelOptions { SingleReader = true });
    private readonly Dictionary<string, Room> _rooms = new(StringComparer.OrdinalIgnoreCase);
    private Task? _loop;

    private sealed class Room(string name)
    {
        public string Name { get; } = name;
        public string? Topic { get; set; }
        public HashSet<ClientSession> Members { get; } = [];
        public List<MsgPayload> History { get; } = [];
    }

    public void Start() => _loop = Task.Run(async () =>
    {
        await foreach (var command in _commands.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                command();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"RoomEngine command failed: {ex}");
            }
        }
    });

    public async ValueTask StopAsync()
    {
        _commands.Writer.TryComplete();
        if (_loop is not null)
        {
            await _loop.ConfigureAwait(false);
        }
    }

    public ValueTask DispatchAsync(ClientSession session, BanterEnvelope envelope, object payload) =>
        _commands.Writer.WriteAsync(() => Handle(session, envelope, payload));

    public ValueTask DisconnectAsync(ClientSession session) =>
        _commands.Writer.WriteAsync(() => RemoveFromAllRooms(session, "disconnected"));

    private void Handle(ClientSession session, BanterEnvelope envelope, object payload)
    {
        switch (payload)
        {
            case JoinPayload join:
                HandleJoin(session, envelope, join);
                break;
            case PartPayload part:
                HandlePart(session, envelope, part);
                break;
            case MsgPayload msg:
                HandleMsg(session, envelope, msg);
                break;
            case TopicPayload topic:
                HandleTopic(session, envelope, topic);
                break;
            case HistoryReqPayload history:
                HandleHistory(session, envelope, history);
                break;
            case RoomListPayload:
                session.Send(new RoomListPayload(
                    _rooms.Values.Select(r => new RoomSummary(r.Name, r.Topic, r.Members.Count)).ToArray()),
                    replyTo: envelope.MsgId);
                break;
            case RoomMembersPayload members:
                HandleMembers(session, envelope, members);
                break;
            default:
                session.Send(new ErrorPayload("UNSUPPORTED", $"{envelope.Type} is not supported yet."), replyTo: envelope.MsgId);
                break;
        }
    }

    private void HandleJoin(ClientSession session, BanterEnvelope envelope, JoinPayload join)
    {
        if (!RoomName.IsValid(join.Room))
        {
            session.Send(new ErrorPayload("BAD_ROOM", $"'{join.Room}' is not a valid room name."), replyTo: envelope.MsgId);
            return;
        }

        if (!_rooms.TryGetValue(join.Room, out var room))
        {
            room = new Room(join.Room);
            _rooms[room.Name] = room;
        }

        if (room.Members.Add(session))
        {
            Broadcast(room, new JoinPayload(room.Name, session.Nick));
        }

        session.Send(new OkPayload(), replyTo: envelope.MsgId);
    }

    private void HandlePart(ClientSession session, BanterEnvelope envelope, PartPayload part)
    {
        if (_rooms.TryGetValue(part.Room, out var room) && room.Members.Remove(session))
        {
            Broadcast(room, new PartPayload(room.Name, part.Reason, session.Nick));
            session.Send(new PartPayload(room.Name, part.Reason, session.Nick));
        }

        session.Send(new OkPayload(), replyTo: envelope.MsgId);
    }

    private void HandleMsg(ClientSession session, BanterEnvelope envelope, MsgPayload msg)
    {
        if (!TryGetJoinedRoom(session, envelope, msg.Room, out var room))
        {
            return;
        }

        var authoritative = msg with
        {
            Sender = session.Nick,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            MessageId = Guid.NewGuid().ToString("N"),
        };

        room.History.Add(authoritative);
        if (room.History.Count > HistoryCapPerRoom)
        {
            room.History.RemoveAt(0);
        }

        // Echo to every member including the sender — the echo carries the authoritative
        // id/timestamp and doubles as delivery confirmation.
        Broadcast(room, authoritative);
    }

    private void HandleTopic(ClientSession session, BanterEnvelope envelope, TopicPayload topic)
    {
        if (!TryGetJoinedRoom(session, envelope, topic.Room, out var room))
        {
            return;
        }

        room.Topic = topic.Topic;
        Broadcast(room, topic);
    }

    private void HandleHistory(ClientSession session, BanterEnvelope envelope, HistoryReqPayload request)
    {
        if (!TryGetJoinedRoom(session, envelope, request.Room, out var room))
        {
            return;
        }

        var limit = Math.Clamp(request.Limit, 1, 500);
        var end = room.History.Count;
        if (request.BeforeMessageId is not null)
        {
            end = room.History.FindIndex(m => m.MessageId == request.BeforeMessageId);
            if (end < 0)
            {
                session.Send(new ErrorPayload("BAD_CURSOR", "Unknown history cursor."), replyTo: envelope.MsgId);
                return;
            }
        }

        var start = Math.Max(0, end - limit);
        var page = room.History[start..end];
        var nextCursor = start > 0 ? page[0].MessageId : null;
        session.Send(new HistoryChunkPayload(room.Name, page, nextCursor), replyTo: envelope.MsgId);
    }

    private void HandleMembers(ClientSession session, BanterEnvelope envelope, RoomMembersPayload request)
    {
        if (!_rooms.TryGetValue(request.Room, out var room))
        {
            session.Send(new ErrorPayload("NO_SUCH_ROOM", $"{request.Room} does not exist."), replyTo: envelope.MsgId);
            return;
        }

        session.Send(new RoomMembersPayload(
            room.Name,
            room.Members.Select(m => new MemberInfo(m.Nick, m.IsAgent, "")).ToArray()),
            replyTo: envelope.MsgId);
    }

    private bool TryGetJoinedRoom(ClientSession session, BanterEnvelope envelope, string roomName, out Room room)
    {
        if (_rooms.TryGetValue(roomName, out room!) && room.Members.Contains(session))
        {
            return true;
        }

        session.Send(new ErrorPayload("NOT_IN_ROOM", $"You are not in {roomName}."), replyTo: envelope.MsgId);
        return false;
    }

    private void RemoveFromAllRooms(ClientSession session, string reason)
    {
        foreach (var room in _rooms.Values)
        {
            if (room.Members.Remove(session))
            {
                Broadcast(room, new PartPayload(room.Name, reason, session.Nick));
            }
        }
    }

    private static void Broadcast<TPayload>(Room room, TPayload payload) where TPayload : notnull
    {
        foreach (var member in room.Members)
        {
            member.Send(payload);
        }
    }
}
