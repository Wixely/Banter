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

    /// <summary>Monotonic counter giving each agent a stable join order for election tie-breaks.</summary>
    private long _joinSequence;

    private sealed class Room(string name, string? topic)
    {
        public string Name { get; } = name;
        public string? Topic { get; set; } = topic;
        public HashSet<ClientSession> Members { get; } = [];

        /// <summary>Dispatch mode (PLAN §8a). Delegated is the default.</summary>
        public RoomDispatchMode Mode { get; set; } = RoomDispatchMode.Delegated;

        /// <summary>
        /// Most sensitive content this room may carry. Defaults to <see cref="DataSensitivity.Sensitive"/>
        /// so an unclassified room gets the strict election rule rather than the permissive one.
        /// </summary>
        public DataSensitivity Sensitivity { get; set; } = DataSensitivity.Sensitive;

        /// <summary>Agents present, by nick, with the attributes they announced.</summary>
        public Dictionary<string, AgentCandidate> Agents { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Current delegator's nick, or null when none is elected.</summary>
        public string? Delegator { get; set; }

        /// <summary>Parent room when this is a sub-room, so the side channel stays traceable
        /// back to the conversation that spawned it.</summary>
        public string? ParentRoom { get; set; }

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

            await RemoveFromAllRoomsAsync(session, "disconnected").ConfigureAwait(false);
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
                await HandlePartAsync(session, envelope, part).ConfigureAwait(false);
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
            case AgentAnnouncePayload announce:
                await HandleAgentAnnounceAsync(session, envelope, announce).ConfigureAwait(false);
                break;
            case AgentListPayload agents:
                HandleAgentList(session, envelope, agents);
                break;
            case RoomModePayload mode:
                await HandleRoomModeAsync(session, envelope, mode).ConfigureAwait(false);
                break;
            case RoomCreatePayload create:
                await HandleRoomCreateAsync(session, envelope, create).ConfigureAwait(false);
                break;
            case AgentMovePayload move:
                await HandleAgentMoveAsync(session, envelope, move).ConfigureAwait(false);
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

            if (session.IsAgent)
            {
                // An agent that has not announced still enters the roster, with Unknown
                // attributes — which the election treats as frontier and uncleared. Leaving it
                // out entirely would be worse: it would be invisible to a human asking who is
                // in the room, while still reading everything said there.
                room.Agents[session.Nick] = session.Announcement is { } announced
                    ? ToCandidate(announced, _joinSequence++)
                    : new AgentCandidate(session.Nick, AgentLocality.Unknown, DataSensitivity.Unknown,
                        [], CostTier: 1, JoinSequence: _joinSequence++);

                await ReelectAsync(room).ConfigureAwait(false);
            }
        }

        // Tell the joiner how the room dispatches and who is dispatching, so an agent knows on
        // arrival whether to stay quiet rather than answering once before finding out.
        session.Send(new RoomModePayload(room.Name, room.Mode));
        session.Send(new RoomDelegatorPayload(room.Name, room.Delegator));
        session.Send(new OkPayload(), replyTo: envelope.MsgId);
    }

    private async ValueTask HandlePartAsync(ClientSession session, BanterEnvelope envelope, PartPayload part)
    {
        if (_rooms.TryGetValue(part.Room, out var room) && room.Members.Remove(session))
        {
            Broadcast(room, new PartPayload(room.Name, part.Reason, session.Nick));
            session.Send(new PartPayload(room.Name, part.Reason, session.Nick));

            if (room.Agents.Remove(session.Nick))
            {
                await ReelectAsync(room).ConfigureAwait(false);
            }
        }

        session.Send(new OkPayload(), replyTo: envelope.MsgId);
    }

    private static AgentCandidate ToCandidate(AgentAnnouncePayload a, long joinSequence) =>
        new(a.Nick, a.Locality, a.Clearance, a.Skills, a.CostTier, joinSequence, a.WantsDelegator);

    /// <summary>
    /// Re-run the election and, if the outcome changed, announce it. Announcing only on change is
    /// what keeps a busy room from narrating an election on every join, and the announcement is
    /// what makes the choice auditable from the timeline rather than an invisible server decision.
    /// </summary>
    private async ValueTask ReelectAsync(Room room)
    {
        var result = DelegatorElection.Elect([.. room.Agents.Values], room.Sensitivity);
        if (string.Equals(result.Nick, room.Delegator, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        room.Delegator = result.Nick;
        Broadcast(room, new RoomDelegatorPayload(room.Name, result.Nick, result.Reason));

        var text = result.Nick is null
            ? $"No delegator: {result.Reason}. Agents answer when mentioned."
            : $"{result.Nick} is now the delegator for this room ({result.Reason}).";
        await AnnounceSystemAsync(room, text).ConfigureAwait(false);
    }

    private async ValueTask HandleAgentAnnounceAsync(
        ClientSession session, BanterEnvelope envelope, AgentAnnouncePayload announce)
    {
        if (!session.IsAgent)
        {
            // Attributes decide who may read a room's traffic, so only accounts the server
            // already recognises as agents may claim them.
            session.Send(new ErrorPayload("NOT_AN_AGENT", "Only agent accounts may announce capabilities."),
                replyTo: envelope.MsgId);
            return;
        }

        // The announcement is always attributed to the authenticated nick, never to whatever the
        // payload claims — otherwise an agent could announce on another's behalf.
        var owned = announce with { Nick = session.Nick };
        session.Announcement = owned;

        foreach (var room in _rooms.Values.Where(r => r.Members.Contains(session)))
        {
            var existing = room.Agents.TryGetValue(session.Nick, out var current)
                ? current.JoinSequence
                : _joinSequence++;
            room.Agents[session.Nick] = ToCandidate(owned, existing);
            await ReelectAsync(room).ConfigureAwait(false);
        }

        session.Send(new OkPayload(), replyTo: envelope.MsgId);
    }

    private void HandleAgentList(ClientSession session, BanterEnvelope envelope, AgentListPayload request)
    {
        if (!_rooms.TryGetValue(request.Room, out var room) || !room.Members.Contains(session))
        {
            session.Send(new ErrorPayload("NOT_IN_ROOM", $"You are not in {request.Room}."), replyTo: envelope.MsgId);
            return;
        }

        session.Send(
            new AgentListPayload(
                room.Name,
                room.Agents.Values
                    .Select(a => new AgentInfoPayload(
                        a.Nick, a.Locality, a.Clearance, a.Skills, "", a.CostTier,
                        string.Equals(a.Nick, room.Delegator, StringComparison.OrdinalIgnoreCase)))
                    .ToArray()),
            replyTo: envelope.MsgId);
    }

    /// <summary>
    /// Open a room, optionally as a child of one the caller is in. A sub-room inherits its
    /// parent's sensitivity and mode: anything looser would make "open a sub-room" a way to move
    /// a sensitive conversation somewhere a frontier agent is eligible to read it.
    /// </summary>
    private async ValueTask HandleRoomCreateAsync(
        ClientSession session, BanterEnvelope envelope, RoomCreatePayload request)
    {
        if (!RoomName.IsValid(request.Room))
        {
            session.Send(new ErrorPayload("BAD_ROOM", $"'{request.Room}' is not a valid room name."),
                replyTo: envelope.MsgId);
            return;
        }

        Room? parent = null;
        if (request.ParentRoom is { Length: > 0 } parentName)
        {
            if (!_rooms.TryGetValue(parentName, out parent) || !parent.Members.Contains(session))
            {
                session.Send(new ErrorPayload("NOT_IN_ROOM", $"You are not in {parentName}."),
                    replyTo: envelope.MsgId);
                return;
            }
        }

        if (_rooms.TryGetValue(request.Room, out var existing))
        {
            // Already exists: joining is the caller's next move, but never silently relax an
            // existing room's sensitivity to match a new parent.
            session.Send(new RoomCreatePayload(existing.Name, request.ParentRoom, request.Purpose),
                replyTo: envelope.MsgId);
            return;
        }

        var room = new Room(request.Room, topic: request.Purpose.Length > 0 ? request.Purpose : null);
        if (parent is not null)
        {
            room.Sensitivity = parent.Sensitivity;
            room.Mode = parent.Mode;
            room.ParentRoom = parent.Name;
        }

        _rooms[room.Name] = room;
        await store.UpsertRoomAsync(room.Name, room.Topic).ConfigureAwait(false);

        // The creator joins immediately: a sub-room nobody is in is not useful, and the
        // delegator needs to be in it to run it.
        room.Members.Add(session);
        if (session.IsAgent)
        {
            room.Agents[session.Nick] = session.Announcement is { } announced
                ? ToCandidate(announced, _joinSequence++)
                : new AgentCandidate(session.Nick, AgentLocality.Unknown, DataSensitivity.Unknown,
                    [], CostTier: 1, JoinSequence: _joinSequence++);
        }

        await ReelectAsync(room).ConfigureAwait(false);

        if (parent is not null)
        {
            // Link it from the parent, so the side channel is discoverable from the main
            // conversation rather than being a room only the agents know about.
            await AnnounceSystemAsync(
                parent,
                request.Purpose.Length > 0
                    ? $"{session.Nick} opened {room.Name} for: {request.Purpose}"
                    : $"{session.Nick} opened {room.Name}").ConfigureAwait(false);
        }

        session.Send(new RoomModePayload(room.Name, room.Mode));
        session.Send(new RoomDelegatorPayload(room.Name, room.Delegator));
        session.Send(new RoomCreatePayload(room.Name, room.ParentRoom, request.Purpose), replyTo: envelope.MsgId);
    }

    /// <summary>
    /// Move an agent into a room on a delegator's behalf. The clearance check is the point of
    /// this handler: without it a delegator could pull a frontier agent into a sensitive
    /// sub-room, which is exactly the egress the routing rules exist to prevent.
    /// </summary>
    private async ValueTask HandleAgentMoveAsync(
        ClientSession session, BanterEnvelope envelope, AgentMovePayload request)
    {
        if (!_rooms.TryGetValue(request.Room, out var room) || !room.Members.Contains(session))
        {
            session.Send(new ErrorPayload("NOT_IN_ROOM", $"You are not in {request.Room}."),
                replyTo: envelope.MsgId);
            return;
        }

        if (!string.Equals(room.Delegator, session.Nick, StringComparison.OrdinalIgnoreCase))
        {
            session.Send(new ErrorPayload("NOT_DELEGATOR", $"Only {room.Name}'s delegator may move agents into it."),
                replyTo: envelope.MsgId);
            return;
        }

        if (!_sessionsByNick.TryGetValue(request.Nick, out var targets) || targets.Count == 0)
        {
            session.Send(new ErrorPayload("NO_SUCH_USER", $"'{request.Nick}' has no live session."),
                replyTo: envelope.MsgId);
            return;
        }

        var target = targets.First();
        if (!target.IsAgent)
        {
            session.Send(new ErrorPayload("NOT_AN_AGENT", "Only agents can be moved between rooms."),
                replyTo: envelope.MsgId);
            return;
        }

        var candidate = target.Announcement is { } announced
            ? ToCandidate(announced, 0)
            : new AgentCandidate(target.Nick, AgentLocality.Unknown, DataSensitivity.Unknown, [], 1, 0);

        if (!DelegatorElection.CanReceive(candidate, room.Sensitivity))
        {
            session.Send(
                new ErrorPayload(
                    "NOT_CLEARED",
                    $"'{request.Nick}' is not cleared for {room.Sensitivity.ToString().ToLowerInvariant()} " +
                    $"content in {room.Name}."),
                replyTo: envelope.MsgId);
            return;
        }

        foreach (var moved in targets.Where(t => room.Members.Add(t)).ToList())
        {
            Broadcast(room, new JoinPayload(room.Name, moved.Nick));
            moved.Send(new RoomModePayload(room.Name, room.Mode));
            moved.Send(new RoomDelegatorPayload(room.Name, room.Delegator));
        }

        room.Agents[target.Nick] = candidate with { JoinSequence = _joinSequence++ };
        await ReelectAsync(room).ConfigureAwait(false);

        if (request.Reason.Length > 0)
        {
            await AnnounceSystemAsync(room, $"{request.Nick} was brought in: {request.Reason}").ConfigureAwait(false);
        }

        session.Send(new OkPayload(), replyTo: envelope.MsgId);
    }

    private async ValueTask HandleRoomModeAsync(ClientSession session, BanterEnvelope envelope, RoomModePayload request)
    {
        if (!_rooms.TryGetValue(request.Room, out var room) || !room.Members.Contains(session))
        {
            session.Send(new ErrorPayload("NOT_IN_ROOM", $"You are not in {request.Room}."), replyTo: envelope.MsgId);
            return;
        }

        if (room.Mode != request.Mode)
        {
            room.Mode = request.Mode;
            Broadcast(room, new RoomModePayload(room.Name, room.Mode));
            await AnnounceSystemAsync(room, room.Mode == RoomDispatchMode.Delegated
                ? "Room is now delegated: one agent routes requests."
                : "Room is now mention mode: agents answer when named.").ConfigureAwait(false);
        }

        session.Send(new RoomModePayload(room.Name, room.Mode), replyTo: envelope.MsgId);
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

    private async ValueTask RemoveFromAllRoomsAsync(ClientSession session, string reason)
    {
        foreach (var room in _rooms.Values)
        {
            if (!room.Members.Remove(session))
            {
                continue;
            }

            Broadcast(room, new PartPayload(room.Name, reason, session.Nick));

            // A delegator whose socket drops would otherwise leave the room with a dispatcher
            // that is not there, and every request unanswered.
            if (room.Agents.Remove(session.Nick))
            {
                await ReelectAsync(room).ConfigureAwait(false);
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
