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
internal sealed class RoomEngine(
    IServerStore store,
    AgentGuardrails? guardrails = null,
    TaskStore? tasks = null,
    TaskLimits? taskLimits = null,
    Tools.IToolBroker? tools = null,
    IAgentIdentityStore? identities = null)
{
    private readonly AgentGuardrails _guardrails = guardrails ?? AgentGuardrails.Default;
    private readonly TaskStore? _tasks = tasks;
    private readonly TaskLimits _taskLimits = taskLimits ?? TaskLimits.Default;
    private readonly Tools.IToolBroker? _tools = tools;

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

    /// <summary>
    /// Ends every live session signed in as <paramref name="nick"/>, each told <paramref name="reason"/>
    /// on its way out. On the command loop because that is where the nick table is honest;
    /// fire-and-forget per session because each eviction ends with that session's own teardown
    /// posting back onto this very loop.
    /// </summary>
    /// <param name="except">A session to spare — the one that asked, when the subject is themselves.</param>
    public ValueTask EvictAsync(string nick, string reason, ClientSession? except = null) =>
        _commands.Writer.WriteAsync(() =>
        {
            foreach (var session in SessionsFor(nick).Where(s => !ReferenceEquals(s, except)).ToList())
            {
                _ = session.EvictAsync(reason);
            }

            return ValueTask.CompletedTask;
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
            case EditPayload edit:
                await HandleEditAsync(session, envelope, edit).ConfigureAwait(false);
                break;
            case DeletePayload delete:
                await HandleDeleteAsync(session, envelope, delete).ConfigureAwait(false);
                break;
            case RoomListPayload:
                session.Send(new RoomListPayload(
                    _rooms.Values
                        .Select(r => new RoomSummary(
                            r.Name,
                            r.Topic,
                            // Count people, not connections: a user on two devices is one member.
                            r.Members.Select(m => m.Nick).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                            r.ParentRoom))
                        .ToArray()),
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
            case TaskPostPayload post:
                await HandleTaskPostAsync(session, envelope, post).ConfigureAwait(false);
                break;
            case TaskClaimPayload claimTask:
                await HandleTaskClaimAsync(session, envelope, claimTask).ConfigureAwait(false);
                break;
            case TaskAssignPayload assign:
                await HandleTaskAssignAsync(session, envelope, assign).ConfigureAwait(false);
                break;
            case TaskUpdatePayload taskUpdate:
                await HandleTaskUpdateAsync(session, envelope, taskUpdate).ConfigureAwait(false);
                break;
            case TaskReleasePayload release:
                await HandleTaskReleaseAsync(session, envelope, release).ConfigureAwait(false);
                break;
            case TaskDonePayload done:
                await HandleTaskDoneAsync(session, envelope, done).ConfigureAwait(false);
                break;
            case TaskListPayload taskList:
                await HandleTaskListAsync(session, envelope, taskList).ConfigureAwait(false);
                break;
            case ToolListPayload:
                await HandleToolListAsync(session, envelope).ConfigureAwait(false);
                break;
            case ToolCallPayload call:
                HandleToolCall(session, envelope, call);
                break;
            case ToolGrantsPayload grants:
                await HandleToolGrantsAsync(session, envelope, grants).ConfigureAwait(false);
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

        // Presence is per USER, delivery is per SESSION. Banter does not use IRC's
        // alice / alice^mobile convention: identity is the account, and a session is one of its
        // connections, so a second device must not read as a second person arriving.
        var alreadyPresent = HasSessionInRoom(room, session.Nick);
        if (room.Members.Add(session) && !alreadyPresent)
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
        if (_rooms.TryGetValue(part.Room, out var room))
        {
            // An explicit part is the person leaving, not the device: leaving on your laptop and
            // still receiving the room on your phone would be baffling. So every session of this
            // account leaves together.
            var leaving = SessionsFor(session.Nick).Where(room.Members.Contains).ToList();
            if (leaving.Count > 0)
            {
                foreach (var other in leaving)
                {
                    room.Members.Remove(other);
                }

                Broadcast(room, new PartPayload(room.Name, part.Reason, session.Nick));
                foreach (var other in leaving)
                {
                    other.Send(new PartPayload(room.Name, part.Reason, session.Nick));
                }

                if (room.Agents.Remove(session.Nick))
                {
                    await ReelectAsync(room).ConfigureAwait(false);
                }
            }
        }

        session.Send(new OkPayload(), replyTo: envelope.MsgId);
    }

    /// <summary>Every live session for a nick, including ones not in any room.</summary>
    private IEnumerable<ClientSession> SessionsFor(string nick) =>
        _sessionsByNick.TryGetValue(nick, out var sessions) ? sessions.ToList() : [];

    /// <summary>Whether this account already has a session in the room.</summary>
    private static bool HasSessionInRoom(Room room, string nick, ClientSession? excluding = null) =>
        room.Members.Any(m => !ReferenceEquals(m, excluding)
                              && string.Equals(m.Nick, nick, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Put every connected admin into a room and announce it. Silent addition would be worse than
    /// none: an operator should be able to see that they are watching, and so should the agents.
    /// </summary>
    private void AddAdmins(Room room)
    {
        foreach (var admin in _sessionsByNick.Values.SelectMany(s => s).Where(s => s.IsAdmin).ToList())
        {
            if (HasSessionInRoom(room, admin.Nick))
            {
                continue;
            }

            if (room.Members.Add(admin))
            {
                Broadcast(room, new JoinPayload(room.Name, admin.Nick));
                admin.Send(new RoomModePayload(room.Name, room.Mode));
                admin.Send(new RoomDelegatorPayload(room.Name, room.Delegator));
            }
        }
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
        var owned = await ClampToIdentityAsync(announce with { Nick = session.Nick }).ConfigureAwait(false);
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

    /// <summary>
    /// The announcement is a request; the identity is the answer. Locality, clearance and skills
    /// decide what an agent may see and be handed, so for an agent the admin manages they come
    /// from the identity record — what the agent announced is a default that stands only where no
    /// identity exists (legacy password agents) or where the record has nothing to say. The agent
    /// keeps CostTier and WantsDelegator: preferences, not permissions, and the identity does not
    /// store them.
    /// </summary>
    private async ValueTask<AgentAnnouncePayload> ClampToIdentityAsync(AgentAnnouncePayload announced)
    {
        if (identities is null
            || await identities.FindAsync(announced.Nick).ConfigureAwait(false) is not { } identity)
        {
            return announced;
        }

        return announced with
        {
            Locality = identity.Locality,
            Clearance = identity.Clearance,
            Skills = identity.Skills.Count > 0 ? identity.Skills : announced.Skills,
        };
    }

    /// <summary>
    /// Re-applies an identity's attributes to any session already announced under it, and
    /// re-elects where it matters — so an admin's change on the agents page takes effect on the
    /// live agent now, not on whenever it next happens to reconnect.
    /// </summary>
    public ValueTask ReapplyIdentityAsync(string nick) =>
        _commands.Writer.WriteAsync(async () =>
        {
            foreach (var session in SessionsFor(nick))
            {
                if (session.Announcement is not { } announced)
                {
                    continue;
                }

                var owned = await ClampToIdentityAsync(announced).ConfigureAwait(false);
                session.Announcement = owned;

                foreach (var room in _rooms.Values.Where(r => r.Members.Contains(session)))
                {
                    if (room.Agents.TryGetValue(nick, out var current))
                    {
                        room.Agents[nick] = ToCandidate(owned, current.JoinSequence);
                    }

                    await ReelectAsync(room).ConfigureAwait(false);
                }
            }
        });

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

        // Oversight rule (PLAN §8a): every room an agent opens has the operators in it. An agent
        // that could open a room humans cannot see would be able to hold the whole conversation
        // somewhere nobody is watching, which defeats the point of the timeline being the audit
        // trail.
        if (session.IsAgent)
        {
            AddAdmins(room);
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

    // ── Work ledger (PLAN §8b) ───────────────────────────────────────────────────────────────

    private async ValueTask HandleTaskPostAsync(ClientSession session, BanterEnvelope envelope, TaskPostPayload post)
    {
        if (_tasks is null || !_rooms.TryGetValue(post.Room, out var room) || !room.Members.Contains(session))
        {
            session.Send(new ErrorPayload("NOT_IN_ROOM", $"You are not in {post.Room}."), replyTo: envelope.MsgId);
            return;
        }

        if (string.IsNullOrWhiteSpace(post.Title))
        {
            session.Send(new ErrorPayload("BAD_TASK", "A task needs a title."), replyTo: envelope.MsgId);
            return;
        }

        var task = await _tasks.CreateAsync(room.Name, post.Title.Trim(), post.Body, session.Nick, post.LeaseSeconds)
            .ConfigureAwait(false);

        Broadcast(room, task);
        await AnnounceSystemAsync(room, $"task {Short(task.TaskId)} posted by {session.Nick}: {task.Title}")
            .ConfigureAwait(false);
        session.Send(task, replyTo: envelope.MsgId);
    }

    private async ValueTask HandleTaskClaimAsync(ClientSession session, BanterEnvelope envelope, TaskClaimPayload claim)
    {
        var task = _tasks is null ? null : await _tasks.GetAsync(claim.TaskId).ConfigureAwait(false);
        if (task is null || !_rooms.TryGetValue(task.Room, out var room) || !room.Members.Contains(session))
        {
            session.Send(new ErrorPayload("NO_SUCH_TASK", "No such task in a room you are in."),
                replyTo: envelope.MsgId);
            return;
        }

        if (!session.IsAgent)
        {
            session.Send(new ErrorPayload("NOT_AN_AGENT", "Only agents claim tasks."), replyTo: envelope.MsgId);
            return;
        }

        if (await _tasks!.HeldCountAsync(session.Nick).ConfigureAwait(false) >= _taskLimits.MaxConcurrentPerAgent)
        {
            // Stops a greedy agent hoovering up the board and starving everyone else.
            session.Send(
                new ErrorPayload("TASK_LIMIT", $"You already hold {_taskLimits.MaxConcurrentPerAgent} task(s)."),
                replyTo: envelope.MsgId);
            return;
        }

        var taken = await _tasks.TryTakeAsync(
            claim.TaskId, session.Nick, TaskState.Claimed, _taskLimits.DefaultLeaseSeconds).ConfigureAwait(false);

        if (taken is null)
        {
            // Lost the race, or it was never open. A clean refusal beats duplicate work.
            session.Send(new ErrorPayload("TASK_TAKEN", "That task is no longer open."), replyTo: envelope.MsgId);
            return;
        }

        Broadcast(room, taken);
        await AnnounceSystemAsync(room, $"task {Short(taken.TaskId)} claimed by {session.Nick}").ConfigureAwait(false);
        session.Send(taken, replyTo: envelope.MsgId);
    }

    private async ValueTask HandleTaskAssignAsync(
        ClientSession session, BanterEnvelope envelope, TaskAssignPayload assign)
    {
        var task = _tasks is null ? null : await _tasks.GetAsync(assign.TaskId).ConfigureAwait(false);
        if (task is null || !_rooms.TryGetValue(task.Room, out var room) || !room.Members.Contains(session))
        {
            session.Send(new ErrorPayload("NO_SUCH_TASK", "No such task in a room you are in."),
                replyTo: envelope.MsgId);
            return;
        }

        // Assignment is the delegator's power (PLAN §8b); claiming is everyone's.
        if (!string.Equals(room.Delegator, session.Nick, StringComparison.OrdinalIgnoreCase))
        {
            session.Send(new ErrorPayload("NOT_DELEGATOR", $"Only {room.Name}'s delegator may assign tasks."),
                replyTo: envelope.MsgId);
            return;
        }

        if (!room.Agents.ContainsKey(assign.Nick))
        {
            session.Send(new ErrorPayload("NO_SUCH_USER", $"'{assign.Nick}' is not an agent in {room.Name}."),
                replyTo: envelope.MsgId);
            return;
        }

        var taken = await _tasks!.TryTakeAsync(
            assign.TaskId, assign.Nick, TaskState.Assigned, _taskLimits.DefaultLeaseSeconds).ConfigureAwait(false);

        if (taken is null)
        {
            session.Send(new ErrorPayload("TASK_TAKEN", "That task is no longer open."), replyTo: envelope.MsgId);
            return;
        }

        Broadcast(room, taken);
        await AnnounceSystemAsync(room, $"task {Short(taken.TaskId)} assigned to {assign.Nick} by {session.Nick}")
            .ConfigureAwait(false);
        session.Send(taken, replyTo: envelope.MsgId);
    }

    private async ValueTask HandleTaskUpdateAsync(
        ClientSession session, BanterEnvelope envelope, TaskUpdatePayload update)
    {
        var task = _tasks is null ? null : await _tasks.GetAsync(update.TaskId).ConfigureAwait(false);
        if (task is null || !_rooms.TryGetValue(task.Room, out var room))
        {
            session.Send(new ErrorPayload("NO_SUCH_TASK", "No such task."), replyTo: envelope.MsgId);
            return;
        }

        // A progress note renews the lease: that is how a long job stays held without a separate
        // heartbeat verb, and why going quiet is what loses the work.
        if (!await _tasks!.TryRenewAsync(update.TaskId, session.Nick, _taskLimits.DefaultLeaseSeconds)
            .ConfigureAwait(false))
        {
            session.Send(new ErrorPayload("NOT_HOLDER", "You do not hold that task."), replyTo: envelope.MsgId);
            return;
        }

        await AnnounceSystemAsync(room, $"task {Short(update.TaskId)}: {update.Note}").ConfigureAwait(false);
        session.Send(new OkPayload(), replyTo: envelope.MsgId);
    }

    private async ValueTask HandleTaskReleaseAsync(
        ClientSession session, BanterEnvelope envelope, TaskReleasePayload release)
    {
        var task = _tasks is null ? null : await _tasks.GetAsync(release.TaskId).ConfigureAwait(false);
        if (task is null || !_rooms.TryGetValue(task.Room, out var room))
        {
            session.Send(new ErrorPayload("NO_SUCH_TASK", "No such task."), replyTo: envelope.MsgId);
            return;
        }

        var isDelegator = string.Equals(room.Delegator, session.Nick, StringComparison.OrdinalIgnoreCase);
        if (!await _tasks!.TryReleaseAsync(release.TaskId, isDelegator ? null : session.Nick).ConfigureAwait(false))
        {
            session.Send(new ErrorPayload("NOT_HOLDER", "You do not hold that task."), replyTo: envelope.MsgId);
            return;
        }

        var released = await _tasks.GetAsync(release.TaskId).ConfigureAwait(false);
        if (released is not null)
        {
            Broadcast(room, released);
        }

        await AnnounceSystemAsync(
            room,
            release.Reason.Length > 0
                ? $"task {Short(release.TaskId)} released by {session.Nick}: {release.Reason}"
                : $"task {Short(release.TaskId)} released by {session.Nick}").ConfigureAwait(false);
        session.Send(new OkPayload(), replyTo: envelope.MsgId);
    }

    private async ValueTask HandleTaskDoneAsync(ClientSession session, BanterEnvelope envelope, TaskDonePayload done)
    {
        var task = _tasks is null ? null : await _tasks.GetAsync(done.TaskId).ConfigureAwait(false);
        if (task is null || !_rooms.TryGetValue(task.Room, out var room))
        {
            session.Send(new ErrorPayload("NO_SUCH_TASK", "No such task."), replyTo: envelope.MsgId);
            return;
        }

        if (!await _tasks!.TryFinishAsync(done.TaskId, session.Nick, done.Success, done.Result).ConfigureAwait(false))
        {
            session.Send(new ErrorPayload("NOT_HOLDER", "You do not hold that task."), replyTo: envelope.MsgId);
            return;
        }

        var finished = await _tasks.GetAsync(done.TaskId).ConfigureAwait(false);
        if (finished is not null)
        {
            Broadcast(room, finished);
        }

        await AnnounceSystemAsync(
            room,
            done.Success
                ? $"task {Short(done.TaskId)} done by {session.Nick}{Suffix(done.Result)}"
                : $"task {Short(done.TaskId)} FAILED for {session.Nick}{Suffix(done.Result)}").ConfigureAwait(false);
        session.Send(new OkPayload(), replyTo: envelope.MsgId);
    }

    private async ValueTask HandleTaskListAsync(ClientSession session, BanterEnvelope envelope, TaskListPayload request)
    {
        if (_tasks is null || !_rooms.TryGetValue(request.Room, out var room) || !room.Members.Contains(session))
        {
            session.Send(new ErrorPayload("NOT_IN_ROOM", $"You are not in {request.Room}."), replyTo: envelope.MsgId);
            return;
        }

        var tasks = await _tasks.ListAsync(room.Name, request.IncludeFinished).ConfigureAwait(false);
        session.Send(new TaskListPayload(room.Name, tasks, request.IncludeFinished), replyTo: envelope.MsgId);
    }

    /// <summary>
    /// Answer "what tools do I have?". An agent sees only what it was granted; an admin sees the
    /// whole connected catalogue, which is what the management UI lists to grant from. Nobody
    /// else gets an answer at all — a human client has no business calling tools (PLAN §8).
    /// </summary>
    private async ValueTask HandleToolListAsync(ClientSession session, BanterEnvelope envelope)
    {
        if (_tools is null)
        {
            session.Send(new ErrorPayload("NO_TOOLS", "This server has no tool backend."), replyTo: envelope.MsgId);
            return;
        }

        if (session.IsAdmin)
        {
            session.Send(new ToolListPayload(_tools.AllTools()), replyTo: envelope.MsgId);
            return;
        }

        if (!session.IsAgent)
        {
            session.Send(new ErrorPayload("NOT_AN_AGENT", "Tools are for agents."), replyTo: envelope.MsgId);
            return;
        }

        var granted = await _tools.ToolsForAsync(session.Nick).ConfigureAwait(false);
        session.Send(new ToolListPayload(granted), replyTo: envelope.MsgId);
    }

    /// <summary>
    /// Run a tool on behalf of an agent.
    ///
    /// <para>Deliberately <b>not</b> awaited on the engine loop. A tool call may run for minutes,
    /// and this loop is the single writer for every room on the server — awaiting here would stop
    /// all chat until the tool returned. The call runs off-loop and the result goes straight to
    /// the caller's outbox; the audit line comes back through the loop to reach the room safely.</para>
    /// </summary>
    private void HandleToolCall(ClientSession session, BanterEnvelope envelope, ToolCallPayload call)
    {
        if (_tools is null)
        {
            session.Send(new ErrorPayload("NO_TOOLS", "This server has no tool backend."), replyTo: envelope.MsgId);
            return;
        }

        if (!session.IsAgent)
        {
            // Clients never call tools. The server holds the credentials precisely so that a
            // compromised or careless client cannot reach anything with them.
            session.Send(new ErrorPayload("NOT_AN_AGENT", "Tools are for agents."), replyTo: envelope.MsgId);
            return;
        }

        var replyTo = envelope.MsgId;
        var agent = session.Nick;
        var room = call.Room;

        _ = Task.Run(async () =>
        {
            try
            {
                var result = await _tools.CallAsync(agent, call, audit: line => AuditToRoom(room, line))
                    .ConfigureAwait(false);
                session.Send(result, replyTo);
            }
            catch (Exception ex)
            {
                // The agent is waiting on a reply; a silent failure would hang its turn forever.
                session.Send(new ToolResultPayload(call.Name, ex.Message, IsError: true), replyTo);
            }
        });
    }

    /// <summary>
    /// Put a tool-use line in front of the operator. Tool calls are the one place an agent reaches
    /// outside the chat, so they are announced in the room rather than only logged — the point of
    /// the admin being in every room is to be able to see this happen.
    /// </summary>
    private void AuditToRoom(string room, string line)
    {
        Console.Error.WriteLine($"tool: {line}");
        if (room.Length == 0)
        {
            return;
        }

        _commands.Writer.TryWrite(() =>
        {
            if (_rooms.TryGetValue(room, out var target))
            {
                Broadcast(target, new MsgPayload(
                    target.Name, "server", $"[tool] {line}",
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    null,
                    Guid.NewGuid().ToString("N")));
            }

            return ValueTask.CompletedTask;
        });
    }

    /// <summary>
    /// Read or replace an agent's grants. Admin only, and the read is admin-only too: an agent
    /// that could read other agents' grants would learn the shape of the tool estate it was
    /// deliberately not given.
    /// </summary>
    private async ValueTask HandleToolGrantsAsync(
        ClientSession session, BanterEnvelope envelope, ToolGrantsPayload grants)
    {
        if (_tools is null)
        {
            session.Send(new ErrorPayload("NO_TOOLS", "This server has no tool backend."), replyTo: envelope.MsgId);
            return;
        }

        if (!session.IsAdmin)
        {
            session.Send(new ErrorPayload("NOT_ADMIN", "Only an admin may read or change tool grants."),
                replyTo: envelope.MsgId);
            return;
        }

        if (grants.Agent.Length == 0)
        {
            session.Send(new ErrorPayload("BAD_AGENT", "Name the agent whose grants you mean."),
                replyTo: envelope.MsgId);
            return;
        }

        if (grants.Replace)
        {
            // Only ever grant tools the server actually has: a grant for a name nothing serves is
            // a silent lie in the UI, and it would quietly come alive if an upstream later
            // published that name.
            var known = _tools.AllTools().Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
            var wanted = grants.Tools.Where(known.Contains).ToList();
            await _tools.SetGrantsAsync(grants.Agent, wanted).ConfigureAwait(false);
            Console.Error.WriteLine($"tool: {session.Nick} set {grants.Agent}'s grants to {wanted.Count} tool(s)");
        }

        var current = await _tools.GrantsForAsync(grants.Agent).ConfigureAwait(false);
        session.Send(new ToolGrantsPayload(grants.Agent, current, Replace: false), replyTo: envelope.MsgId);
    }

    /// <summary>
    /// Reclaim tasks whose lease lapsed. Runs on the engine loop like everything else, so a
    /// reclaim cannot interleave with a claim and hand the same task to two agents.
    /// </summary>
    public ValueTask SweepExpiredTasksAsync() =>
        _commands.Writer.WriteAsync(async () =>
        {
            if (_tasks is null)
            {
                return;
            }

            foreach (var expired in await _tasks.ExpiredAsync().ConfigureAwait(false))
            {
                if (!await _tasks.TryReleaseAsync(expired.TaskId, nick: null).ConfigureAwait(false))
                {
                    continue;
                }

                if (_rooms.TryGetValue(expired.Room, out var room))
                {
                    var released = await _tasks.GetAsync(expired.TaskId).ConfigureAwait(false);
                    if (released is not null)
                    {
                        Broadcast(room, released);
                    }

                    await AnnounceSystemAsync(
                        room,
                        $"task {Short(expired.TaskId)} lease expired and was released " +
                        $"(was held by {expired.Assignee})").ConfigureAwait(false);
                }
            }
        });

    /// <summary>Short id for announcements; the full id still travels on the payload.</summary>
    private static string Short(string taskId) => taskId.Length <= 8 ? taskId : taskId[..8];

    private static string Suffix(string result) => result.Length > 0 ? $": {result}" : "";

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

    /// <summary>
    /// Change what a message says. <b>Only the author</b>, deliberately: an operator rewriting
    /// someone else's line would be putting words in their mouth under their name, and a room
    /// where that is possible is one where nothing anybody reads can be trusted. Moderation is
    /// <see cref="HandleDeleteAsync"/>, which takes words away rather than substituting them.
    /// </summary>
    private async ValueTask HandleEditAsync(ClientSession session, BanterEnvelope envelope, EditPayload edit)
    {
        if (!TryGetJoinedRoom(session, envelope, edit.Room, out var room))
        {
            return;
        }

        if (edit.Text.Length == 0)
        {
            // An edit to nothing is a delete, and going through the other verb keeps one rule for
            // who may remove what.
            session.Send(
                new ErrorPayload("EMPTY_EDIT", "An edit needs text. To remove a message, delete it."),
                replyTo: envelope.MsgId);
            return;
        }

        var existing = await store.GetMessageAsync(edit.Room, edit.MessageId).ConfigureAwait(false);
        if (existing is null || existing.DeletedAt is not null)
        {
            session.Send(
                new ErrorPayload("NO_SUCH_MESSAGE", "That message is not in this room."),
                replyTo: envelope.MsgId);
            return;
        }

        if (!string.Equals(existing.Sender, session.Nick, StringComparison.OrdinalIgnoreCase))
        {
            session.Send(
                new ErrorPayload("NOT_YOURS", "Only the person who wrote a message may edit it."),
                replyTo: envelope.MsgId);
            return;
        }

        var editedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (!await store.EditMessageAsync(edit.Room, edit.MessageId, edit.Text, editedAt).ConfigureAwait(false))
        {
            session.Send(
                new ErrorPayload("NO_SUCH_MESSAGE", "That message is not in this room."),
                replyTo: envelope.MsgId);
            return;
        }

        Broadcast(room, edit with { Sender = existing.Sender, EditedAt = editedAt });
    }

    /// <summary>
    /// Take a message back. The author may remove their own; an admin may remove anyone's, which
    /// is the moderation path. The words go from storage — a delete that only stops clients
    /// drawing them has not done what it was asked.
    /// </summary>
    private async ValueTask HandleDeleteAsync(ClientSession session, BanterEnvelope envelope, DeletePayload delete)
    {
        if (!TryGetJoinedRoom(session, envelope, delete.Room, out var room))
        {
            return;
        }

        var existing = await store.GetMessageAsync(delete.Room, delete.MessageId).ConfigureAwait(false);
        if (existing is null || existing.DeletedAt is not null)
        {
            // Deleting an already-deleted message is not an error worth raising, but saying it is
            // gone is honest and idempotent for a client retrying.
            session.Send(
                new ErrorPayload("NO_SUCH_MESSAGE", "That message is not in this room."),
                replyTo: envelope.MsgId);
            return;
        }

        var mine = string.Equals(existing.Sender, session.Nick, StringComparison.OrdinalIgnoreCase);
        if (!mine && !session.IsAdmin)
        {
            session.Send(
                new ErrorPayload("NOT_YOURS", "Only the author or an admin may delete a message."),
                replyTo: envelope.MsgId);
            return;
        }

        var deletedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (!await store.DeleteMessageAsync(delete.Room, delete.MessageId, deletedAt).ConfigureAwait(false))
        {
            session.Send(
                new ErrorPayload("NO_SUCH_MESSAGE", "That message is not in this room."),
                replyTo: envelope.MsgId);
            return;
        }

        // The author is named, not whoever pressed delete: the room is being told whose message
        // went, and an admin removing something is visible in that it went at all.
        Broadcast(room, delete with { Sender = existing.Sender, DeletedAt = deletedAt });
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
            .Select(m => new MsgPayload(
                m.Room, m.Sender, m.Text, m.Timestamp, m.FileId, m.MessageId,
                m.EditedAt ?? 0, m.DeletedAt ?? 0))
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
            // One person is one entry, however many devices they are logged in on.
            room.Members
                .GroupBy(m => m.Nick, StringComparer.OrdinalIgnoreCase)
                .Select(g => new MemberInfo(g.Key, g.First().IsAgent, ""))
                .ToArray()),
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

            // Only announce when the account's LAST session leaves: shutting a laptop while the
            // phone is still connected is not leaving the room.
            if (HasSessionInRoom(room, session.Nick))
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
