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
internal sealed class RoomEngine(IServerStore store, AgentGuardrails? guardrails = null)
{
    private readonly AgentGuardrails _guardrails = guardrails ?? AgentGuardrails.Default;

    private readonly Channel<Func<ValueTask>> _commands = Channel.CreateUnbounded<Func<ValueTask>>(
        new UnboundedChannelOptions { SingleReader = true });
    private readonly Dictionary<string, Room> _rooms = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<ClientSession>> _sessionsByNick = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ActiveStream> _streams = new(StringComparer.Ordinal);
    private Task? _loop;

    /// <summary>An in-flight streamed message. Deltas accumulate so a sender that vanishes
    /// mid-stream still leaves a complete message in the room rather than a dangling one.</summary>
    private sealed class ActiveStream(string streamId, string room, ClientSession owner)
    {
        public string StreamId { get; } = streamId;
        public string Room { get; } = room;
        public ClientSession Owner { get; } = owner;
        public System.Text.StringBuilder Accumulated { get; } = new();
    }

    private sealed class Room(string name, string? topic)
    {
        public string Name { get; } = name;
        public string? Topic { get; set; } = topic;
        public HashSet<ClientSession> Members { get; } = [];

        /// <summary>Timestamps of recent agent messages, for the sliding-window rate limit.</summary>
        public Queue<long> AgentMessageTimes { get; } = new();

        /// <summary>Agent messages since the last human message — the loop-breaker's counter.</summary>
        public int ConsecutiveAgentMessages { get; set; }

        /// <summary>True once the loop-breaker tripped; cleared when a human speaks.</summary>
        public bool LoopBroken { get; set; }
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

    /// <summary>Registers an authenticated session in the nick directory (multi-device: one
    /// nick may have several live sessions; all receive private messages).</summary>
    public ValueTask RegisterAsync(ClientSession session) =>
        _commands.Writer.WriteAsync(() =>
        {
            if (!_sessionsByNick.TryGetValue(session.Nick, out var sessions))
            {
                sessions = [];
                _sessionsByNick[session.Nick] = sessions;
            }

            sessions.Add(session);
            return ValueTask.CompletedTask;
        });

    /// <summary>Membership check for session-side handlers (file transfer), answered inside the
    /// single-writer loop so it can't race a join/part.</summary>
    public async ValueTask<bool> IsMemberAsync(ClientSession session, string room)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await _commands.Writer.WriteAsync(() =>
        {
            tcs.TrySetResult(_rooms.TryGetValue(room, out var r) && r.Members.Contains(session));
            return ValueTask.CompletedTask;
        }).ConfigureAwait(false);
        return await tcs.Task.ConfigureAwait(false);
    }

    public async ValueTask<string[]> GetMemberRoomsAsync(ClientSession session)
    {
        var tcs = new TaskCompletionSource<string[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        await _commands.Writer.WriteAsync(() =>
        {
            tcs.TrySetResult(_rooms.Values.Where(r => r.Members.Contains(session)).Select(r => r.Name).ToArray());
            return ValueTask.CompletedTask;
        }).ConfigureAwait(false);
        return await tcs.Task.ConfigureAwait(false);
    }

    /// <summary>Emits the room message that announces an uploaded file, with the file reference
    /// clients render inline (§5a). Persisted + broadcast like any other message.</summary>
    public ValueTask AnnounceFileAsync(ClientSession session, string room, string fileId, string name) =>
        _commands.Writer.WriteAsync(async () =>
        {
            if (!_rooms.TryGetValue(room, out var target) || !target.Members.Contains(session))
            {
                return;
            }

            var announcement = new MsgPayload(
                room, session.Nick, name,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                fileId,
                Guid.NewGuid().ToString("N"));
            await store.AppendMessageAsync(new ChatMessage(
                announcement.MessageId!, room, announcement.Sender, announcement.Text,
                announcement.Timestamp, fileId)).ConfigureAwait(false);
            Broadcast(target, announcement);
        });

    public ValueTask DisconnectAsync(ClientSession session) =>
        _commands.Writer.WriteAsync(async () =>
        {
            // Close any stream this session left open, so the room never sees a half-written
            // message with no end — the accumulated deltas become the final text.
            foreach (var orphan in _streams.Values.Where(s => ReferenceEquals(s.Owner, session)).ToArray())
            {
                _streams.Remove(orphan.StreamId);
                if (orphan.Accumulated.Length > 0)
                {
                    await CompleteStreamAsync(orphan, orphan.Accumulated.ToString()).ConfigureAwait(false);
                }
            }

            RemoveFromAllRooms(session, "disconnected");
            if (_sessionsByNick.TryGetValue(session.Nick, out var sessions))
            {
                sessions.Remove(session);
                if (sessions.Count == 0)
                {
                    _sessionsByNick.Remove(session.Nick);
                }
            }
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
            case PrivMsgPayload priv:
                HandlePrivMsg(session, envelope, priv);
                break;
            case MsgStreamStartPayload start:
                await HandleStreamStartAsync(session, envelope, start).ConfigureAwait(false);
                break;
            case MsgStreamDeltaPayload delta:
                HandleStreamDelta(session, envelope, delta);
                break;
            case MsgStreamEndPayload end:
                await HandleStreamEndAsync(session, envelope, end).ConfigureAwait(false);
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

        var verdict = await CheckGuardrailsAsync(room, session).ConfigureAwait(false);
        if (verdict != GuardrailVerdict.Allowed)
        {
            session.Send(GuardrailError(verdict), replyTo: envelope.MsgId);
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

    private void HandlePrivMsg(ClientSession session, BanterEnvelope envelope, PrivMsgPayload priv)
    {
        if (!_sessionsByNick.TryGetValue(priv.Recipient, out var recipients))
        {
            session.Send(new ErrorPayload("NO_SUCH_USER", $"{priv.Recipient} is not online."), replyTo: envelope.MsgId);
            return;
        }

        // Sender and timestamp are authoritative; not persisted (room history is room-scoped).
        var authoritative = priv with
        {
            Sender = session.Nick,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };

        foreach (var recipient in recipients)
        {
            recipient.Send(authoritative);
        }

        // Echo to the sender's other sessions (multi-device), not the sending one — its
        // confirmation is the Ok reply.
        if (_sessionsByNick.TryGetValue(session.Nick, out var senders))
        {
            foreach (var other in senders.Where(s => !ReferenceEquals(s, session)))
            {
                other.Send(authoritative);
            }
        }

        session.Send(new OkPayload(), replyTo: envelope.MsgId);
    }

    // ---- Agent guardrails (§5) ----

    /// <summary>
    /// Decides whether a room will relay this sender's message. Humans always pass, and a human
    /// message is what clears a tripped loop-breaker. Agents are rate-limited over a sliding
    /// minute and cut off entirely once they have talked among themselves for too long.
    /// </summary>
    private async ValueTask<GuardrailVerdict> CheckGuardrailsAsync(Room room, ClientSession session)
    {
        if (!_guardrails.Enabled)
        {
            return GuardrailVerdict.Allowed;
        }

        if (!session.IsAgent)
        {
            room.ConsecutiveAgentMessages = 0;
            if (room.LoopBroken)
            {
                room.LoopBroken = false;
                await AnnounceSystemAsync(room, "Loop-breaker cleared: a human spoke, agents may reply again.")
                    .ConfigureAwait(false);
            }

            return GuardrailVerdict.Allowed;
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        while (room.AgentMessageTimes.Count > 0 && now - room.AgentMessageTimes.Peek() > 60_000)
        {
            room.AgentMessageTimes.Dequeue();
        }

        if (room.LoopBroken)
        {
            return GuardrailVerdict.LoopBroken;
        }

        if (room.AgentMessageTimes.Count >= _guardrails.MaxAgentMessagesPerMinute)
        {
            return GuardrailVerdict.RateLimited;
        }

        if (room.ConsecutiveAgentMessages >= _guardrails.MaxConsecutiveAgentMessages)
        {
            room.LoopBroken = true;
            await AnnounceSystemAsync(
                room,
                $"Loop-breaker tripped: {_guardrails.MaxConsecutiveAgentMessages} agent messages with no human input. " +
                "Agents are muted in this room until a human speaks.").ConfigureAwait(false);
            return GuardrailVerdict.LoopBroken;
        }

        room.AgentMessageTimes.Enqueue(now);
        room.ConsecutiveAgentMessages++;
        return GuardrailVerdict.Allowed;
    }

    private static ErrorPayload GuardrailError(GuardrailVerdict verdict) => verdict switch
    {
        GuardrailVerdict.RateLimited => new ErrorPayload("THROTTLED", "This room's agent message rate limit is exceeded; retry shortly."),
        GuardrailVerdict.LoopBroken => new ErrorPayload("LOOP_BROKEN", "This room's loop-breaker is active; a human must speak before agents resume."),
        _ => new ErrorPayload("REFUSED", "The room refused the message."),
    };

    /// <summary>Posts a message attributed to the system nick — persisted like any other, so
    /// the timeline explains why the agents went quiet.</summary>
    private async ValueTask AnnounceSystemAsync(Room room, string text)
    {
        var announcement = new MsgPayload(
            room.Name,
            AgentGuardrails.SystemNick,
            text,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            null,
            Guid.NewGuid().ToString("N"));
        await store.AppendMessageAsync(new ChatMessage(
            announcement.MessageId!, room.Name, announcement.Sender, announcement.Text,
            announcement.Timestamp, null)).ConfigureAwait(false);
        Broadcast(room, announcement);
    }

    // ---- Streamed messages (agent token streams, §4) ----

    private async ValueTask HandleStreamStartAsync(ClientSession session, BanterEnvelope envelope, MsgStreamStartPayload start)
    {
        if (!TryGetJoinedRoom(session, envelope, start.Room, out var room))
        {
            return;
        }

        // A stream is one message, so it costs one message against the guardrails — charged at
        // START, before any tokens flow.
        var verdict = await CheckGuardrailsAsync(room, session).ConfigureAwait(false);
        if (verdict != GuardrailVerdict.Allowed)
        {
            session.Send(GuardrailError(verdict), replyTo: envelope.MsgId);
            return;
        }

        if (string.IsNullOrWhiteSpace(start.StreamId) || _streams.ContainsKey(start.StreamId))
        {
            session.Send(new ErrorPayload("BAD_STREAM_ID", "Stream id is missing or already in use."), replyTo: envelope.MsgId);
            return;
        }

        _streams[start.StreamId] = new ActiveStream(start.StreamId, room.Name, session);
        Broadcast(room, new MsgStreamStartPayload(room.Name, session.Nick, start.StreamId));
        session.Send(new OkPayload(), replyTo: envelope.MsgId);
    }

    private void HandleStreamDelta(ClientSession session, BanterEnvelope envelope, MsgStreamDeltaPayload delta)
    {
        if (!TryGetOwnedStream(session, envelope, delta.StreamId, out var stream))
        {
            return;
        }

        stream.Accumulated.Append(delta.Delta);
        if (_rooms.TryGetValue(stream.Room, out var room))
        {
            Broadcast(room, delta);
        }
    }

    private async ValueTask HandleStreamEndAsync(ClientSession session, BanterEnvelope envelope, MsgStreamEndPayload end)
    {
        if (!TryGetOwnedStream(session, envelope, end.StreamId, out var stream))
        {
            return;
        }

        _streams.Remove(stream.StreamId);
        await CompleteStreamAsync(stream, end.FinalText).ConfigureAwait(false);
    }

    /// <summary>Persists the streamed message and relays the authoritative END. Shared by the
    /// normal path and the sender-vanished path.</summary>
    private async ValueTask CompleteStreamAsync(ActiveStream stream, string finalText)
    {
        if (!_rooms.TryGetValue(stream.Room, out var room))
        {
            return;
        }

        var messageId = Guid.NewGuid().ToString("N");
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await store.AppendMessageAsync(new ChatMessage(
            messageId, stream.Room, stream.Owner.Nick, finalText, timestamp, null)).ConfigureAwait(false);
        Broadcast(room, new MsgStreamEndPayload(stream.StreamId, finalText, timestamp, messageId));
    }

    private bool TryGetOwnedStream(ClientSession session, BanterEnvelope envelope, string streamId, out ActiveStream stream)
    {
        if (_streams.TryGetValue(streamId, out stream!) && ReferenceEquals(stream.Owner, session))
        {
            return true;
        }

        session.Send(new ErrorPayload("NO_SUCH_STREAM", "No stream with that id belongs to you."), replyTo: envelope.MsgId);
        return false;
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
