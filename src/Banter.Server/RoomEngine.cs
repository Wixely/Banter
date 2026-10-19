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
internal sealed partial class RoomEngine(
    IServerStore store,
    AgentGuardrails? guardrails = null,
    TaskStore? tasks = null,
    TaskLimits? taskLimits = null,
    Tools.IToolBroker? tools = null,
    IAgentIdentityStore? identities = null,
    PresenceLimits? presence = null)
{
    private readonly AgentGuardrails _guardrails = guardrails ?? AgentGuardrails.Default;
    private readonly TaskStore? _tasks = tasks;
    private readonly TaskLimits _taskLimits = taskLimits ?? TaskLimits.Default;
    private readonly PresenceLimits _presence = presence ?? PresenceLimits.Default;
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

        /// <summary>
        /// Accounts whose last session has dropped but which are still held present, and the
        /// moment each stops being. A session returning before then cancels the departure and
        /// nothing is ever said about it (<see cref="PresenceLimits"/>).
        ///
        /// <para>On the room rather than in one engine-wide map: leaving is per room, an account
        /// can be in several, and a rejoin to one of them says nothing about the others.</para>
        /// </summary>
        public Dictionary<string, DateTimeOffset> Leaving { get; } =
            new(StringComparer.OrdinalIgnoreCase);

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

        /// <summary>Whether the current delegator is a local agent - remembered, because by the
        /// time it matters the delegator has left and its attributes have left with it.</summary>
        public bool DelegatorIsLocal { get; set; }

        /// <summary>How the delegator may be chosen. See <see cref="RoomRegime"/>.</summary>
        public RoomRegime Regime { get; set; } = RoomRegime.Republic;

        /// <summary>Who may draw on this room from elsewhere. See <see cref="RoomDisclosure"/>.</summary>
        public RoomDisclosure Disclosure { get; set; } = RoomDisclosure.Members;

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
            _rooms[persisted.Name] = new Room(persisted.Name, persisted.Topic)
            {
                ParentRoom = persisted.Parent,
                Disclosure = Enum.IsDefined((RoomDisclosure)persisted.Disclosure)
                    ? (RoomDisclosure)persisted.Disclosure
                    : RoomDisclosure.Members,
            };
        }

        // A parent that no longer exists - closed while the server was down, say - leaves its
        // children at the top level rather than hanging off a name nothing answers to.
        foreach (var orphan in _rooms.Values.Where(r => r.ParentRoom is { } p && !_rooms.ContainsKey(p)))
        {
            orphan.ParentRoom = null;
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

            await CloseAsksFromAsync(session.Nick).ConfigureAwait(false);
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
            case AskPayload ask:
                await HandleAskAsync(session, envelope, ask).ConfigureAwait(false);
                break;
            case AnswerPayload answer:
                await HandleAnswerAsync(session, envelope, answer).ConfigureAwait(false);
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
                            r.ParentRoom,
                            r.Disclosure))
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
            case RoomClosePayload close:
                await HandleRoomCloseAsync(session, envelope, close).ConfigureAwait(false);
                break;
            case RoomRegimePayload regime:
                await HandleRoomRegimeAsync(session, envelope, regime).ConfigureAwait(false);
                break;
            case RoomMovePayload roomMove:
                await HandleRoomMoveAsync(session, envelope, roomMove).ConfigureAwait(false);
                break;
            case RoomDisclosurePayload disclosure:
                await HandleRoomDisclosureAsync(session, envelope, disclosure).ConfigureAwait(false);
                break;
            case RoomRecallPayload recall:
                await HandleRoomRecallAsync(session, envelope, recall).ConfigureAwait(false);
                break;
            case AgentMovePayload move:
                await HandleAgentMoveAsync(session, envelope, move).ConfigureAwait(false);
                break;
            case KickPayload kick:
                await HandleKickAsync(session, envelope, kick).ConfigureAwait(false);
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
            case HubInspectPayload:
                await HandleHubInspectAsync(session, envelope).ConfigureAwait(false);
                break;
            case HubRotatePayload rotate:
                await HandleHubChangeAsync(session, envelope, rotate.Hub, rotate.Agent, rotate: true)
                    .ConfigureAwait(false);
                break;
            case HubForgetPayload forget:
                await HandleHubChangeAsync(session, envelope, forget.Hub, forget.Agent, rotate: false)
                    .ConfigureAwait(false);
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

        // Back inside the grace, so nothing was ever said about leaving and nothing is said about
        // arriving either. The pair is the point: announcing the return of somebody the room was
        // never told had gone is the same noise with half the words.
        var returning = room.Leaving.Remove(session.Nick);

        if (room.Members.Add(session) && !alreadyPresent)
        {
            if (!returning)
            {
                Broadcast(room, new JoinPayload(room.Name, session.Nick));
            }

            // Outside that: only the ANNOUNCEMENT is held back for a return. The agent was taken
            // out of the roster and the election re-run when its socket dropped, because a room
            // with an absent dispatcher answers nothing - so a returning agent still has to be put
            // back, and skipping this with the announcement would cost the room its delegator
            // permanently.
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
        session.Send(new RoomRegimePayload(room.Name, room.Regime));
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
                admin.Send(new RoomRegimePayload(room.Name, room.Regime));
            }
        }
    }

    private static AgentCandidate ToCandidate(AgentAnnouncePayload a, long joinSequence) =>
        new(a.Nick, a.Locality, a.Clearance, a.Skills, a.CostTier, joinSequence, a.WantsDelegator, a.WorkMode);

    /// <summary>
    /// Re-run the election and, if the outcome changed, announce it. Announcing only on change is
    /// what keeps a busy room from narrating an election on every join, and the announcement is
    /// what makes the choice auditable from the timeline rather than an invisible server decision.
    /// </summary>
    private async ValueTask ReelectAsync(Room room)
    {
        // The local agent holding the seat has gone. Before anything is elected in its place, the
        // room goes into interregnum: the election below would otherwise be free to hand the seat
        // to whoever is left, and a third-party agent holding it means every word in the room
        // reaching that third party with nobody having decided it should.
        if (room.Regime == RoomRegime.Republic && room.Delegator is { } sitting && room.DelegatorIsLocal
            && !room.Agents.ContainsKey(sitting))
        {
            await SetRegimeAsync(room, RoomRegime.Interregnum, $"{sitting}, the local delegator, left")
                .ConfigureAwait(false);
        }

        // In interregnum only local agents stand. The operator-configured delegator rule wins
        // outright in the election, ahead of every eligibility check, so this is the only place a
        // third-party agent that asked for the seat can be kept out of it.
        List<AgentCandidate> candidates = room.Regime == RoomRegime.Interregnum
            ? [.. room.Agents.Values.Where(a => a.Locality == AgentLocality.Local)]
            : [.. room.Agents.Values];
        var result = DelegatorElection.Elect(candidates, room.Sensitivity);
        if (result.Nick is null && room.Regime == RoomRegime.Interregnum)
        {
            result = result with { Reason = "the seat is held for a local agent" };
        }

        var winner = result.Nick is { } won ? room.Agents.GetValueOrDefault(won) : null;
        room.DelegatorIsLocal = winner?.Locality == AgentLocality.Local;

        // A local agent in the seat is what the interregnum was waiting for.
        if (room.Regime == RoomRegime.Interregnum && room.DelegatorIsLocal)
        {
            await SetRegimeAsync(room, RoomRegime.Republic, $"{winner!.Nick} took the seat").ConfigureAwait(false);
        }

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

    // ── The room tree, and who may draw on a room ─────────────────────────────────────────────

    /// <summary>
    /// The room everybody starts in, whose delegator runs the server's rooms as a whole: it may
    /// move rooms, set any room's disclosure, close any room, and bring agents in or out of any
    /// room - what an admin can do with rooms, short of anything about people.
    /// </summary>
    public const string MainRoom = "#main";

    private bool IsMainDelegator(ClientSession session) =>
        session.IsAgent
        && _rooms.TryGetValue(MainRoom, out var main)
        && string.Equals(main.Delegator, session.Nick, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether <paramref name="room"/> sits anywhere beneath <paramref name="ancestor"/>.
    /// Bounded, so a tree that is somehow cyclic answers rather than spins.</summary>
    private bool IsBeneath(Room room, Room ancestor)
    {
        var at = room.ParentRoom;
        for (var depth = 0; at is not null && depth < 64; depth++)
        {
            if (string.Equals(at, ancestor.Name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            at = _rooms.TryGetValue(at, out var up) ? up.ParentRoom : null;
        }

        return false;
    }

    /// <summary>
    /// Put a room under another, or at the top level. An admin or the main delegator - nobody
    /// else, because where a room sits decides what its Children disclosure reaches, and a room
    /// placed under another to borrow what that room knows is exactly what this must not allow.
    /// </summary>
    private async ValueTask HandleRoomMoveAsync(ClientSession session, BanterEnvelope envelope, RoomMovePayload request)
    {
        if (!_rooms.TryGetValue(request.Room, out var room))
        {
            session.Send(new ErrorPayload("NO_SUCH_ROOM", $"There is no room called {request.Room}."), replyTo: envelope.MsgId);
            return;
        }

        if (!session.IsAdmin && !IsMainDelegator(session))
        {
            session.Send(new ErrorPayload("NOT_ALLOWED", $"Only an admin or {MainRoom}'s delegator may move rooms."),
                replyTo: envelope.MsgId);
            return;
        }

        Room? parent = null;
        if (request.ParentRoom is { Length: > 0 } parentName)
        {
            if (!_rooms.TryGetValue(parentName, out parent))
            {
                session.Send(new ErrorPayload("NO_SUCH_ROOM", $"There is no room called {parentName}."), replyTo: envelope.MsgId);
                return;
            }

            if (ReferenceEquals(parent, room) || IsBeneath(parent, room))
            {
                session.Send(new ErrorPayload("BAD_MOVE", $"{room.Name} cannot go inside itself."), replyTo: envelope.MsgId);
                return;
            }
        }

        room.ParentRoom = parent?.Name;

        // Never more permissive than where it now sits, for the same reason a new side room is not.
        if (parent is not null && parent.Sensitivity > room.Sensitivity)
        {
            room.Sensitivity = parent.Sensitivity;
        }

        await store.SetRoomPlacementAsync(room.Name, room.ParentRoom, (int)room.Disclosure).ConfigureAwait(false);

        // Everybody, not only the room's members: the tree is in every client's room list.
        var moved = new RoomMovePayload(room.Name, room.ParentRoom, session.Nick);
        foreach (var anyone in _sessionsByNick.Values.SelectMany(s => s).ToList())
        {
            anyone.Send(moved);
        }

        await AnnounceSystemAsync(room, room.ParentRoom is { } now
            ? $"{session.Nick} moved this room under {now}."
            : $"{session.Nick} moved this room to the top level.").ConfigureAwait(false);
        session.Send(moved, replyTo: envelope.MsgId);
    }

    /// <summary>
    /// Change who may draw on a room from elsewhere. The room's own delegator, the main
    /// delegator, or an admin. Asking for the disclosure it already has is a read.
    /// </summary>
    private async ValueTask HandleRoomDisclosureAsync(
        ClientSession session, BanterEnvelope envelope, RoomDisclosurePayload request)
    {
        if (!_rooms.TryGetValue(request.Room, out var room))
        {
            session.Send(new ErrorPayload("NO_SUCH_ROOM", $"There is no room called {request.Room}."), replyTo: envelope.MsgId);
            return;
        }

        if (room.Disclosure != request.Disclosure)
        {
            var may = session.IsAdmin
                || IsMainDelegator(session)
                || string.Equals(room.Delegator, session.Nick, StringComparison.OrdinalIgnoreCase);
            if (!may)
            {
                session.Send(
                    new ErrorPayload("NOT_ALLOWED", $"Only an admin, {room.Name}'s delegator or {MainRoom}'s delegator may change this."),
                    replyTo: envelope.MsgId);
                return;
            }

            room.Disclosure = request.Disclosure;
            await store.SetRoomPlacementAsync(room.Name, room.ParentRoom, (int)room.Disclosure).ConfigureAwait(false);
            Broadcast(room, new RoomDisclosurePayload(room.Name, room.Disclosure, session.Nick));
            await AnnounceSystemAsync(room, $"{session.Nick} set this room to {room.Disclosure}: " + room.Disclosure switch
            {
                RoomDisclosure.Isolated => "nothing said here can be drawn on from any other room.",
                RoomDisclosure.Children => "rooms beneath this one may draw on what is said here.",
                RoomDisclosure.Global => "ANY agent may draw on what is said here, from any room, whether or not it is in this one.",
                _ => "agents in this room may draw on it from the other rooms they are in.",
            }).ConfigureAwait(false);
        }

        session.Send(new RoomDisclosurePayload(room.Name, room.Disclosure), replyTo: envelope.MsgId);
    }

    /// <summary>
    /// An agent in <c>FromRoom</c> asking for what was said in <c>Room</c>. The room's disclosure
    /// decides, checked here on every request; clearance applies on top whatever it says.
    /// </summary>
    private async ValueTask HandleRoomRecallAsync(ClientSession session, BanterEnvelope envelope, RoomRecallPayload request)
    {
        if (!_rooms.TryGetValue(request.Room, out var room))
        {
            session.Send(new ErrorPayload("NO_SUCH_ROOM", $"There is no room called {request.Room}."), replyTo: envelope.MsgId);
            return;
        }

        // The room the request is made from is one the asker is in - otherwise Children could be
        // satisfied by naming any side room at all.
        if (!_rooms.TryGetValue(request.FromRoom, out var from) || !from.Members.Contains(session))
        {
            session.Send(new ErrorPayload("NOT_IN_ROOM", $"You are not in {request.FromRoom}."), replyTo: envelope.MsgId);
            return;
        }

        var inside = ReferenceEquals(from, room);
        var allowed = room.Disclosure switch
        {
            RoomDisclosure.Isolated => inside,
            RoomDisclosure.Children => inside || IsBeneath(from, room),
            RoomDisclosure.Global => true,
            _ => room.Members.Contains(session),
        };

        if (!allowed)
        {
            session.Send(new ErrorPayload("NOT_DISCLOSED", room.Disclosure switch
            {
                RoomDisclosure.Isolated => $"{room.Name} is isolated: what is said there stays there.",
                RoomDisclosure.Children => $"{room.Name} is shared only with rooms beneath it, and {from.Name} is not one.",
                _ => $"{room.Name} is shared only with the agents in it.",
            }), replyTo: envelope.MsgId);
            return;
        }

        if (session.IsAgent)
        {
            var candidate = session.Announcement is { } announced
                ? ToCandidate(announced, 0)
                : new AgentCandidate(session.Nick, AgentLocality.Unknown, DataSensitivity.Unknown, [], 1, 0);
            if (!DelegatorElection.CanReceive(candidate, room.Sensitivity))
            {
                session.Send(new ErrorPayload("NOT_CLEARED",
                    $"You are not cleared for {room.Sensitivity.ToString().ToLowerInvariant()} content in {room.Name}."),
                    replyTo: envelope.MsgId);
                return;
            }
        }

        var page = await store.GetHistoryPageAsync(room.Name, null, Math.Clamp(request.Limit, 1, 50)).ConfigureAwait(false);
        var messages = (page?.Messages ?? [])
            .Where(m => m.DeletedAt is null)
            .Select(m => new MsgPayload(m.Room, m.Sender, m.Text, m.Timestamp, m.FileId, m.MessageId, m.EditedAt ?? 0, 0, m.ReplyTo))
            .ToArray();
        session.Send(new HistoryChunkPayload(room.Name, messages, null), replyTo: envelope.MsgId);
    }

    /// <summary>Changes a room's regime and says so, to the room and in its timeline.</summary>
    private async ValueTask SetRegimeAsync(Room room, RoomRegime regime, string reason, string? by = null)
    {
        room.Regime = regime;
        Broadcast(room, new RoomRegimePayload(room.Name, regime, reason, by));
        await AnnounceSystemAsync(room, regime == RoomRegime.Interregnum
            ? $"Interregnum: {reason}. The seat is held for a local agent until one takes it, or an admin ends this."
            : $"Republic restored: {reason}.").ConfigureAwait(false);
    }

    /// <summary>
    /// An admin changing a room's regime. Ending an interregnum is how somebody says a
    /// third-party delegator is intended here; declaring one holds the seat for a local agent
    /// before anything has gone wrong. Either way the election runs again under the new rules.
    /// </summary>
    private async ValueTask HandleRoomRegimeAsync(
        ClientSession session, BanterEnvelope envelope, RoomRegimePayload request)
    {
        if (!_rooms.TryGetValue(request.Room, out var room))
        {
            session.Send(new ErrorPayload("NO_SUCH_ROOM", $"There is no room called {request.Room}."),
                replyTo: envelope.MsgId);
            return;
        }

        if (room.Regime != request.Regime)
        {
            if (!session.IsAdmin)
            {
                session.Send(new ErrorPayload("NOT_ADMIN", "Only an admin may change a room's regime."),
                    replyTo: envelope.MsgId);
                return;
            }

            await SetRegimeAsync(
                room,
                request.Regime,
                request.Regime == RoomRegime.Republic ? $"ended by {session.Nick}" : $"declared by {session.Nick}",
                session.Nick).ConfigureAwait(false);
            await ReelectAsync(room).ConfigureAwait(false);
        }

        session.Send(new RoomRegimePayload(room.Name, room.Regime), replyTo: envelope.MsgId);
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
        var requested = announce with { Nick = session.Nick };
        session.RequestedAnnouncement = requested;
        var owned = await ClampToIdentityAsync(requested).ConfigureAwait(false);
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

        // What the agent actually got, pushed after the Ok. An agent that asked for one thing and
        // was granted another should not have to discover it from a roster; one that predates this
        // push ignores the frame harmlessly.
        session.Send(owned);
    }

    /// <summary>
    /// The announcement is a request; the identity is the answer. Locality, clearance and skills
    /// decide what an agent may see and be handed, so for an agent the admin manages they always
    /// come from the identity record — what the agent announced stands only where no identity
    /// exists (legacy password agents) or where the record has nothing to say. Cost and the
    /// delegator wish are the agent's by default, but an identity override outranks them too:
    /// a self-flagged delegator wins the election outright, which makes the wish a permission in
    /// all but name.
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
            CostTier = identity.CostTier ?? announced.CostTier,
            WantsDelegator = identity.WantsDelegator ?? announced.WantsDelegator,
            WorkMode = identity.WorkMode ?? announced.WorkMode,
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
                // Clamped from the REQUESTED announcement, not the effective one: clearing an
                // override falls back to the agent's own words, which only the request still holds.
                if (session.RequestedAnnouncement is not { } requested)
                {
                    continue;
                }

                var owned = await ClampToIdentityAsync(requested).ConfigureAwait(false);
                session.Announcement = owned;

                // The live agent learns its new attributes the moment the admin sets them, the
                // same push a fresh announce gets.
                session.Send(owned);

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
                        string.Equals(a.Nick, room.Delegator, StringComparison.OrdinalIgnoreCase),
                        a.WorkMode))
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

        // A side room keeps its parent's discretion unless it is asked for something else: work
        // moved out of an isolated room into a side room should not become shareable by moving.
        room.Disclosure = request.Disclosure ?? parent?.Disclosure ?? RoomDisclosure.Members;

        _rooms[room.Name] = room;
        await store.UpsertRoomAsync(room.Name, room.Topic).ConfigureAwait(false);
        await store.SetRoomPlacementAsync(room.Name, room.ParentRoom, (int)room.Disclosure).ConfigureAwait(false);

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
        session.Send(new RoomRegimePayload(room.Name, room.Regime));
        session.Send(new RoomCreatePayload(room.Name, room.ParentRoom, request.Purpose), replyTo: envelope.MsgId);
    }

    /// <summary>
    /// Close a room for everybody in it. Everyone in it is told, everyone is taken out, and it is
    /// forgotten - so it is gone from the list and is not loaded again at start. The history is
    /// kept: closing tidies the list, it does not take back what was said.
    ///
    /// <para>An admin may close any room. An agent may close a room it is the delegator of, or a
    /// side room off one it is the delegator of: the agent that opened a room for a piece of work
    /// is the one that knows when the work is finished, and a side room nobody can close is one
    /// the room list carries forever.</para>
    /// </summary>
    private async ValueTask HandleRoomCloseAsync(
        ClientSession session, BanterEnvelope envelope, RoomClosePayload request)
    {
        if (!_rooms.TryGetValue(request.Room, out var room))
        {
            session.Send(new ErrorPayload("NO_SUCH_ROOM", $"There is no room called {request.Room}."),
                replyTo: envelope.MsgId);
            return;
        }

        Room? parent = room.ParentRoom is { } parentName && _rooms.TryGetValue(parentName, out var p) ? p : null;
        var mayClose = session.IsAdmin
            || IsMainDelegator(session)
            || string.Equals(room.Delegator, session.Nick, StringComparison.OrdinalIgnoreCase)
            || (parent is not null && string.Equals(parent.Delegator, session.Nick, StringComparison.OrdinalIgnoreCase));
        if (!mayClose)
        {
            session.Send(
                new ErrorPayload("NOT_ALLOWED", $"Only an admin or {room.Name}'s delegator may close it."),
                replyTo: envelope.MsgId);
            return;
        }

        var notice = new RoomClosePayload(room.Name, request.Reason, session.Nick);
        foreach (var member in room.Members.ToList())
        {
            member.Send(notice);
        }

        room.Members.Clear();
        room.Agents.Clear();
        room.Leaving.Clear();
        _rooms.Remove(room.Name);
        await store.DeleteRoomAsync(room.Name).ConfigureAwait(false);

        // Its side rooms move up a level rather than hanging off a room that is gone - and keep
        // whatever Children disclosure reached, which is the closed room's own parent now.
        foreach (var child in _rooms.Values.Where(r => string.Equals(r.ParentRoom, room.Name, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            child.ParentRoom = room.ParentRoom;
            await store.SetRoomPlacementAsync(child.Name, child.ParentRoom, (int)child.Disclosure).ConfigureAwait(false);
        }

        // Said where the room came from, so the conversation that opened it also shows it ending.
        if (parent is not null)
        {
            await AnnounceSystemAsync(
                parent,
                request.Reason.Length > 0
                    ? $"{session.Nick} closed {room.Name}: {request.Reason}"
                    : $"{session.Nick} closed {room.Name}").ConfigureAwait(false);
        }

        session.Send(new OkPayload(), replyTo: envelope.MsgId);
    }

    /// <summary>
    /// Take somebody out of a room. An admin may remove anybody; a delegator may remove agents
    /// from the room it runs - the same reach it has for putting them in. Every session of the
    /// account goes, and each is told, so a client and an agent both know to stop treating the
    /// room as theirs rather than finding out on the next thing they try to say.
    /// </summary>
    private async ValueTask HandleKickAsync(ClientSession session, BanterEnvelope envelope, KickPayload request)
    {
        if (!_rooms.TryGetValue(request.Room, out var room))
        {
            session.Send(new ErrorPayload("NO_SUCH_ROOM", $"There is no room called {request.Room}."),
                replyTo: envelope.MsgId);
            return;
        }

        var leaving = room.Members
            .Where(m => string.Equals(m.Nick, request.Nick, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (leaving.Count == 0)
        {
            session.Send(new ErrorPayload("NOT_IN_ROOM", $"{request.Nick} is not in {room.Name}."),
                replyTo: envelope.MsgId);
            return;
        }

        var isAgent = leaving[0].IsAgent;
        var mayRemove = session.IsAdmin
            || (isAgent && (IsMainDelegator(session)
                            || string.Equals(room.Delegator, session.Nick, StringComparison.OrdinalIgnoreCase)));
        if (!mayRemove)
        {
            session.Send(
                new ErrorPayload("NOT_ALLOWED", isAgent
                    ? $"Only an admin or {room.Name}'s delegator may remove an agent from it."
                    : $"Only an admin may remove somebody from {room.Name}."),
                replyTo: envelope.MsgId);
            return;
        }

        var reason = request.Reason is { Length: > 0 } why ? $"removed by {session.Nick}: {why}" : $"removed by {session.Nick}";
        foreach (var member in leaving)
        {
            room.Members.Remove(member);
        }

        room.Leaving.Remove(request.Nick);
        var part = new PartPayload(room.Name, reason, leaving[0].Nick);
        Broadcast(room, part);
        foreach (var member in leaving)
        {
            member.Send(part);
        }

        if (room.Agents.Remove(request.Nick))
        {
            await ReelectAsync(room).ConfigureAwait(false);
        }

        session.Send(new OkPayload(), replyTo: envelope.MsgId);
    }

    /// <summary>
    /// Move an agent into a room on a delegator's behalf. The clearance check is the point of
    /// this handler: without it a delegator could pull a frontier agent into a sensitive
    /// sub-room, which is exactly the egress the routing rules exist to prevent.
    /// </summary>
    private async ValueTask HandleAgentMoveAsync(
        ClientSession session, BanterEnvelope envelope, AgentMovePayload request)
    {
        // An admin may put anybody in any room - person or agent, whether or not the admin is in
        // it - which is how a member who fell out of a room gets back. Everybody else is a
        // delegator moving agents into the room it runs, as before.
        var main = IsMainDelegator(session);
        if (!_rooms.TryGetValue(request.Room, out var room)
            || (!session.IsAdmin && !main && !room.Members.Contains(session)))
        {
            session.Send(new ErrorPayload("NOT_IN_ROOM", $"You are not in {request.Room}."),
                replyTo: envelope.MsgId);
            return;
        }

        if (!session.IsAdmin && !main && !string.Equals(room.Delegator, session.Nick, StringComparison.OrdinalIgnoreCase))
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
            if (!session.IsAdmin)
            {
                session.Send(new ErrorPayload("NOT_AN_AGENT", "Only agents can be moved between rooms."),
                    replyTo: envelope.MsgId);
                return;
            }

            // A person, added by an admin. No clearance to check and no election to run: what
            // they are owed is the room, announced, and told how it dispatches.
            foreach (var added in targets.Where(t => room.Members.Add(t)).ToList())
            {
                Broadcast(room, new JoinPayload(room.Name, added.Nick));
                added.Send(new RoomModePayload(room.Name, room.Mode));
                added.Send(new RoomDelegatorPayload(room.Name, room.Delegator));
                added.Send(new RoomRegimePayload(room.Name, room.Regime));
            }

            await AnnounceSystemAsync(room, $"{target.Nick} was added by {session.Nick}.").ConfigureAwait(false);
            session.Send(new OkPayload(), replyTo: envelope.MsgId);
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
            moved.Send(new RoomRegimePayload(room.Name, room.Regime));
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
        // No room means every room. A room name always starts with '#', so an empty one cannot
        // collide with a real request — and this is the operator's view (PLAN §8a: an admin is in
        // every room an agent opens anyway, so it discloses nothing they could not already read
        // one board at a time).
        //
        // Checked before the store, because "you are not an admin" is true whether or not this
        // server keeps tasks, and answering NOT_IN_ROOM to a request that names no room would
        // send somebody looking for the wrong problem.
        if (request.Room.Length == 0)
        {
            if (!session.IsAdmin)
            {
                session.Send(new ErrorPayload("NOT_ADMIN", "Only an admin may list every room's work."),
                    replyTo: envelope.MsgId);
                return;
            }

            var all = _tasks is null
                ? []
                : await _tasks.ListAllAsync(request.IncludeFinished).ConfigureAwait(false);
            session.Send(new TaskListPayload("", all, request.IncludeFinished), replyTo: envelope.MsgId);
            return;
        }

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
    /// What the hubs this server defers to look like from here.
    ///
    /// <para>Admin-only for the same reason the grants are: it names every agent that has an
    /// identity on a hub and what each holds, which is a map of the estate. It carries no keys —
    /// those stay with the server that calls as the agents.</para>
    /// </summary>
    private async Task HandleHubInspectAsync(ClientSession session, BanterEnvelope envelope)
    {
        if (_tools is null)
        {
            session.Send(new ErrorPayload("NO_TOOLS", "This server has no tool backend."), replyTo: envelope.MsgId);
            return;
        }

        if (!session.IsAdmin)
        {
            session.Send(new ErrorPayload("NOT_ADMIN", "Only an admin may inspect the tool hubs."),
                replyTo: envelope.MsgId);
            return;
        }

        var hubs = await _tools.InspectHubsAsync().ConfigureAwait(false);
        session.Send(new HubReportPayload(hubs), replyTo: envelope.MsgId);
    }

    /// <summary>
    /// Rotating an agent's key on a hub, or removing its identity there. One handler because they
    /// differ in a single call: everything around it - who may ask, what must be named, what the
    /// hub refusing looks like, and what comes back - is the same, and the two drifting apart
    /// would mean one of them quietly stopped being admin-only.
    ///
    /// <para>Answered with the fresh report rather than an Ok. The client that asked is showing
    /// the state this just changed, and a second round trip to find out what it is now is a window
    /// in which the page is wrong. There is also nothing else to hand back: a rotated key stays on
    /// this server, which is the whole point of minting it here.</para>
    /// </summary>
    private async Task HandleHubChangeAsync(
        ClientSession session, BanterEnvelope envelope, string hub, string agent, bool rotate)
    {
        var verb = rotate ? "rotate a key on" : "remove an identity from";

        if (_tools is null)
        {
            session.Send(new ErrorPayload("NO_TOOLS", "This server has no tool backend."), replyTo: envelope.MsgId);
            return;
        }

        if (!session.IsAdmin)
        {
            session.Send(new ErrorPayload("NOT_ADMIN", $"Only an admin may {verb} a tool hub."),
                replyTo: envelope.MsgId);
            return;
        }

        if (hub.Length == 0 || agent.Length == 0)
        {
            session.Send(new ErrorPayload("BAD_AGENT", "Name the hub and the agent you mean."),
                replyTo: envelope.MsgId);
            return;
        }

        try
        {
            if (rotate)
            {
                await _tools.RotateHubKeyAsync(hub, agent).ConfigureAwait(false);
            }
            else
            {
                await _tools.RemoveHubIdentityAsync(hub, agent).ConfigureAwait(false);
            }
        }
        catch (Tools.HubAdminException ex)
        {
            // Caught rather than left to the loop's catch-all, which logs and sends nothing: the
            // client is waiting on a reply, and a page that never hears back shows the old state
            // with no sign that anything failed. The message already carries the hub's remedy -
            // HubAdminException folds it in - which matters because every one of these is fixed on
            // the hub rather than here.
            session.Send(new ErrorPayload("HUB_REFUSED", ex.Message), replyTo: envelope.MsgId);
            return;
        }

        Console.Error.WriteLine(
            $"hub: {session.Nick} {(rotate ? "rotated" : "removed")} {agent}'s identity on {hub}");

        session.Send(
            new HubReportPayload(await _tools.InspectHubsAsync().ConfigureAwait(false)),
            replyTo: envelope.MsgId);
    }

    /// <summary>
    /// Announce the departures whose grace has run out: accounts held present after their last
    /// session dropped that have not come back (<see cref="PresenceLimits"/>).
    ///
    /// <para>On the engine loop like every other mutation, so a sweep cannot interleave with a
    /// JOIN and announce somebody who has just arrived. Checked again here rather than trusted
    /// from the record - a session can be back without having rejoined that room yet, and it is
    /// the account having no session in the room that makes it gone.</para>
    /// </summary>
    public ValueTask SweepDeparturesAsync() =>
        _commands.Writer.WriteAsync(() =>
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var room in _rooms.Values)
            {
                if (room.Leaving.Count == 0)
                {
                    continue;
                }

                foreach (var (nick, due) in room.Leaving.Where(l => l.Value <= now).ToList())
                {
                    room.Leaving.Remove(nick);

                    if (HasSessionInRoom(room, nick))
                    {
                        // Back, by some path that did not clear this. Nothing to say.
                        continue;
                    }

                    // The announcement is all that is left to do: the roster and the election
                    // were settled the moment the socket dropped.
                    Broadcast(room, new PartPayload(room.Name, "disconnected", nick));
                }
            }

            return ValueTask.CompletedTask;
        });

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

            // Only a person may say who acts on their message. Enforced here rather than trusted
            // to every agent: one that honoured another agent's list would let two of them hand
            // work around the delegator.
            To = session.IsAgent ? null : msg.To,
        };

        await store.AppendMessageAsync(new ChatMessage(
            authoritative.MessageId!,
            authoritative.Room,
            authoritative.Sender,
            authoritative.Text,
            authoritative.Timestamp,
            authoritative.FileId)
        {
            ReplyTo = authoritative.ReplyTo,
        }).ConfigureAwait(false);

        // Echo to every member including the sender — the echo carries the authoritative
        // id/timestamp and doubles as delivery confirmation.
        Broadcast(room, authoritative);
    }

    /// <summary>
    /// Says something into a room as <paramref name="session"/>, stored and broadcast exactly as
    /// an ordinary message. Used where a payload also needs to exist as a line of chat — a
    /// question and the answer to it are part of the conversation, and one that existed only as a
    /// payload would be missing from the history and from everyone not watching at the time.
    /// </summary>
    /// <summary>Says something in the room on a session's behalf and returns the message's id, so
    /// a caller that needs to point at what it just said does not have to guess.</summary>
    private async ValueTask<string> RelayAsync(
        ClientSession session, Room room, string text, string? replyTo = null)
    {
        var message = new MsgPayload(
            room.Name, session.Nick, text,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), null,
            Guid.NewGuid().ToString("N"), ReplyTo: replyTo);

        await store.AppendMessageAsync(new ChatMessage(
            message.MessageId!, message.Room, message.Sender, message.Text,
            message.Timestamp, message.FileId) { ReplyTo = replyTo }).ConfigureAwait(false);

        Broadcast(room, message);
        return message.MessageId!;
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
                m.EditedAt ?? 0, m.DeletedAt ?? 0, m.ReplyTo))
            .ToArray();

        // A page is built from whatever happens to be in the room, so its size is not ours to
        // choose - and the limit above is a count, which says nothing about bytes. On a conduit
        // the ceiling is far below Banter's own 4 MB, and a page of long agent replies passes it;
        // the send would then be refused, with the reply never arriving and the request left to
        // time out. So: measure, and trim to fit.
        //
        // Trimmed from the OLDEST end, because the cursor points backwards. Whatever is dropped
        // is exactly what the next page asks for, so nothing is lost - it arrives one request
        // later. Paging already had to work; this only makes it start sooner.
        var frame = session.Encode(
            new HistoryChunkPayload(room.Name, messages, page.NextCursor), replyTo: envelope.MsgId);

        if (frame.Length > session.MaxFrameBytes)
        {
            // Bisect for the most messages that fit. `fits` is a count known to fit (0 trivially
            // does) and `tooMany` one known not to, so the gap closes in about nine probes for a
            // 500-message page - and none of this runs at all for a page that already fitted.
            var fits = 0;
            var tooMany = messages.Length;
            byte[]? best = null;

            while (tooMany - fits > 1)
            {
                var probe = fits + ((tooMany - fits) / 2);
                var candidate = EncodeTail(probe);
                if (candidate.Length <= session.MaxFrameBytes)
                {
                    fits = probe;
                    best = candidate;
                }
                else
                {
                    tooMany = probe;
                }
            }

            if (best is null)
            {
                // One message on its own is over the ceiling. Nothing to trim towards, and it is
                // not a cursor problem, so say so rather than letting the send be refused: bulk
                // that large belongs in a relic, not a frame.
                session.Send(
                    new ErrorPayload(
                        "PAGE_TOO_LARGE",
                        $"A single message in {room.Name} exceeds this connection's " +
                        $"{session.MaxFrameBytes}-byte frame limit."),
                    replyTo: envelope.MsgId);
                return;
            }

            frame = best;
        }

        session.SendEncoded(frame);

        byte[] EncodeTail(int count) =>
            session.Encode(
                new HistoryChunkPayload(room.Name, messages[^count..], messages[^count].MessageId),
                replyTo: envelope.MsgId);
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

            // …and not even then, yet. The account is held present for a moment first, because a
            // phone leaving the foreground drops its socket and comes back - see PresenceLimits.
            // A grace of zero announces immediately, which is what this did before.
            if (_presence.ReconnectGrace > TimeSpan.Zero)
            {
                room.Leaving[session.Nick] = DateTimeOffset.UtcNow + _presence.ReconnectGrace;
            }
            else
            {
                Broadcast(room, new PartPayload(room.Name, reason, session.Nick));
            }

            // Immediately, grace or not: a delegator whose socket drops would otherwise leave the
            // room with a dispatcher that is not there and every request unanswered. Only the
            // ANNOUNCEMENT waits - who may answer is a question about now, not about whether
            // somebody is coming back, so holding it would mean a room that quietly dispatches to
            // a phone in somebody's pocket.
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
