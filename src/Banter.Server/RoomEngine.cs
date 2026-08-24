using System.Threading.Channels;
using Banter.Core;
using Banter.Protocol;
using Banter.Server.Persistence;

namespace Banter.Server;

/// <summary>
/// The authoritative room state machine. All mutations flow through one command channel with a
/// single consumer, so ordering is deterministic and live state needs no locks (PLAN §5).
/// Messages, rooms, and topics persist through <see cref="IServerStore"/>; store awaits happen
/// inside the single-writer loop, which keeps history ordering identical to fan-out ordering.
/// </summary>
internal sealed class RoomEngine(IServerStore store)
{
    private readonly Channel<Func<ValueTask>> _commands = Channel.CreateUnbounded<Func<ValueTask>>(
        new UnboundedChannelOptions { SingleReader = true });
    private readonly Dictionary<string, Room> _rooms = new(StringComparer.OrdinalIgnoreCase);
    private Task? _loop;

    private sealed class Room(string name, string? topic)
    {
        public string Name { get; } = name;
        public string? Topic { get; set; } = topic;
        public HashSet<ClientSession> Members { get; } = [];
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        foreach (var persisted in await store.GetRoomsAsync(cancellationToken).ConfigureAwait(false))
        {
            _rooms[persisted.Name] = new Room(persisted.Name, persisted.Topic);
        }

        _loop = Task.Run(async () =>
        {
            await foreach (var command in _commands.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                try
                {
                    await command().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"RoomEngine command failed: {ex}");
                }
            }
        }, CancellationToken.None);
    }

    public async ValueTask StopAsync()
    {
        _commands.Writer.TryComplete();
        if (_loop is not null)
        {
            await _loop.ConfigureAwait(false);
        }
    }

    public ValueTask DispatchAsync(ClientSession session, BanterEnvelope envelope, object payload) =>
        _commands.Writer.WriteAsync(() => HandleAsync(session, envelope, payload));

    public ValueTask DisconnectAsync(ClientSession session) =>
        _commands.Writer.WriteAsync(() =>
        {
            RemoveFromAllRooms(session, "disconnected");
            return ValueTask.CompletedTask;
        });

    private async ValueTask HandleAsync(ClientSession session, BanterEnvelope envelope, object payload)
    {
        switch (payload)
        {
            case JoinPayload join:
                await HandleJoinAsync(session, envelope, join).ConfigureAwait(false);
                break;
            case PartPayload part:
                HandlePart(session, envelope, part);
                break;
            case MsgPayload msg:
                await HandleMsgAsync(session, envelope, msg).ConfigureAwait(false);
                break;
            case TopicPayload topic:
                await HandleTopicAsync(session, envelope, topic).ConfigureAwait(false);
                break;
            case HistoryReqPayload history:
                await HandleHistoryAsync(session, envelope, history).ConfigureAwait(false);
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

    private async ValueTask HandleJoinAsync(ClientSession session, BanterEnvelope envelope, JoinPayload join)
    {
        if (!RoomName.IsValid(join.Room))
        {
            session.Send(new ErrorPayload("BAD_ROOM", $"'{join.Room}' is not a valid room name."), replyTo: envelope.MsgId);
            return;
        }

        if (!_rooms.TryGetValue(join.Room, out var room))
        {
            room = new Room(join.Room, topic: null);
            _rooms[room.Name] = room;
            await store.UpsertRoomAsync(room.Name, null).ConfigureAwait(false);
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

    private async ValueTask HandleMsgAsync(ClientSession session, BanterEnvelope envelope, MsgPayload msg)
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

        await store.AppendMessageAsync(new ChatMessage(
            authoritative.MessageId!,
            authoritative.Room,
            authoritative.Sender,
            authoritative.Text,
            authoritative.Timestamp,
            authoritative.FileId)).ConfigureAwait(false);

        // Echo to every member including the sender — the echo carries the authoritative
        // id/timestamp and doubles as delivery confirmation.
        Broadcast(room, authoritative);
    }

    private async ValueTask HandleTopicAsync(ClientSession session, BanterEnvelope envelope, TopicPayload topic)
    {
        if (!TryGetJoinedRoom(session, envelope, topic.Room, out var room))
        {
            return;
        }

        room.Topic = topic.Topic;
        await store.UpsertRoomAsync(room.Name, topic.Topic).ConfigureAwait(false);
        Broadcast(room, topic);
    }

    private async ValueTask HandleHistoryAsync(ClientSession session, BanterEnvelope envelope, HistoryReqPayload request)
    {
        if (!TryGetJoinedRoom(session, envelope, request.Room, out var room))
        {
            return;
        }

        var limit = Math.Clamp(request.Limit, 1, 500);
        var page = await store.GetHistoryPageAsync(room.Name, request.BeforeMessageId, limit).ConfigureAwait(false);
        if (page is null)
        {
            session.Send(new ErrorPayload("BAD_CURSOR", "Unknown history cursor."), replyTo: envelope.MsgId);
            return;
        }

        var messages = page.Messages
            .Select(m => new MsgPayload(m.Room, m.Sender, m.Text, m.Timestamp, m.FileId, m.MessageId))
            .ToArray();
        session.Send(new HistoryChunkPayload(room.Name, messages, page.NextCursor), replyTo: envelope.MsgId);
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
