using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Banter.Protocol;
using Banter.Server.Tools;

namespace Banter.Server;

/// <summary>
/// The debug bundle: everything this server knows about itself, its rooms, its agents and what
/// just happened in them, as one JSON document an admin can hand to a developer.
///
/// <para>Built on the command loop, so it is one consistent moment rather than a dozen reads
/// racing the rooms. It carries message text and tool results, as the history does - the admin is
/// in every room already, and a bundle without the conversation would be a bundle without the
/// thing that went wrong in it. It carries no keys: an agent's hub key stays here, as everywhere.</para>
/// </summary>
internal sealed partial class RoomEngine
{
    private static readonly JsonSerializerOptions DebugJson = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private async ValueTask HandleDebugDumpAsync(ClientSession session, BanterEnvelope envelope, DebugDumpPayload request)
    {
        if (!session.IsAdmin)
        {
            session.Send(new ErrorPayload("NOT_ADMIN", "Only an admin may take a debug bundle."), replyTo: envelope.MsgId);
            return;
        }

        // Shrunk to fit the connection rather than refused: a bundle with less history is a
        // bundle, and the person asking for one is usually the person with the least patience
        // left. History first, then the trace tail, then the bundle without either.
        var history = Math.Clamp(request.HistoryPerRoom, 0, 500);
        var traceLines = 500;
        while (true)
        {
            var json = JsonSerializer.Serialize(await BuildDebugReportAsync(history, traceLines).ConfigureAwait(false), DebugJson);
            var frame = session.Encode(new DebugReportPayload(json), replyTo: envelope.MsgId);
            if (frame.Length <= session.MaxFrameBytes)
            {
                session.SendEncoded(frame);
                return;
            }

            if (history > 0)
            {
                history /= 2;
            }
            else if (traceLines > 0)
            {
                traceLines = 0;
            }
            else
            {
                session.Send(new ErrorPayload("TOO_LARGE",
                    $"The bundle exceeds this connection's {session.MaxFrameBytes}-byte frame limit even without history."),
                    replyTo: envelope.MsgId);
                return;
            }
        }
    }

    private async Task<DebugReport> BuildDebugReportAsync(int historyPerRoom, int traceLines)
    {
        var assembly = typeof(RoomEngine).Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                      ?? assembly.GetName().Version?.ToString() ?? "unknown";

        var sessions = _sessionsByNick.Values.SelectMany(s => s).Distinct().ToList();

        var rooms = new List<DebugRoom>();
        foreach (var room in _rooms.Values.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
        {
            IReadOnlyList<DebugMessage> recent = [];
            if (historyPerRoom > 0)
            {
                var page = await store.GetHistoryPageAsync(room.Name, null, historyPerRoom).ConfigureAwait(false);
                recent = (page?.Messages ?? []).Select(m => new DebugMessage(
                    DateTimeOffset.FromUnixTimeMilliseconds(m.Timestamp), m.Sender, m.Text, m.MessageId, m.FileId, m.ReplyTo,
                    m.EditedAt is { } e ? DateTimeOffset.FromUnixTimeMilliseconds(e) : null,
                    m.DeletedAt is { } d ? DateTimeOffset.FromUnixTimeMilliseconds(d) : null)).ToList();
            }

            rooms.Add(new DebugRoom(
                room.Name, room.Topic, room.ParentRoom, room.Mode, room.Regime, room.Disclosure, room.Parley, room.Sensitivity,
                room.Delegator, room.DelegatorIsLocal,
                room.Members.Select(m => m.Nick).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList(),
                room.Agents.Values.OrderBy(a => a.JoinSequence).Select(a => new DebugRoomAgent(
                    a.Nick, a.Locality, a.Clearance, a.Skills, a.CostTier, a.ConfiguredDelegator, a.WorkMode, a.JoinSequence)).ToList(),
                [.. room.Leaving.Keys],
                new DebugGuardrails(room.ConsecutiveAgentMessages, room.LoopBroken, room.AgentMessageTimes.Count,
                    _guardrails.MaxConsecutiveAgentMessages, _guardrails.MaxAgentMessagesPerMinute),
                _asks.Values.Where(a => string.Equals(a.Room, room.Name, StringComparison.OrdinalIgnoreCase))
                    .Select(a => new DebugAsk(a.Ask.AskId, a.Asker, a.Ask.Questions.Select(q => q.Text).ToList())).ToList(),
                recent));
        }

        var debugSessions = new List<DebugSession>();
        foreach (var s in sessions.OrderBy(s => s.Nick, StringComparer.OrdinalIgnoreCase))
        {
            IReadOnlyList<string>? grants = null;
            if (s.IsAgent && _tools is not null)
            {
                try
                {
                    grants = await _tools.GrantsForAsync(s.Nick).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    grants = [$"(could not read: {ex.Message})"];
                }
            }

            debugSessions.Add(new DebugSession(
                s.Nick, s.IsAgent, s.IsAdmin, s.Peer, s.ConnectedAt,
                _rooms.Values.Where(r => r.Members.Contains(s)).Select(r => r.Name).Order(StringComparer.OrdinalIgnoreCase).ToList(),
                s.RequestedAnnouncement is { } asked ? Describe(asked) : null,
                s.Announcement is { } granted ? Describe(granted) : null,
                grants));
        }

        IReadOnlyList<DebugIdentity>? debugIdentities = null;
        if (identities is not null)
        {
            debugIdentities = (await identities.ListAsync().ConfigureAwait(false)).Select(i => new DebugIdentity(
                i.Nick, i.Rooms, i.Skills, i.Locality, i.Clearance, i.CostTier, i.WantsDelegator, i.WorkMode,
                i.Persona, i.LastSdk, i.LastSeenAt, i.PublicKey is not null, i.EnrolmentPending)).ToList();
        }

        DebugTools? debugTools = null;
        if (_tools is not null)
        {
            var upstreams = (_tools as McpToolBroker)?.Upstreams
                .Select(u => new DebugUpstream(u.Key, u.State.ToString(), u.LastError, u.ToolCount)).ToList();
            IReadOnlyList<HubPayload> hubs = [];
            try
            {
                hubs = await _tools.InspectHubsAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // A hub that cannot be asked is already described by its upstream state above.
            }

            debugTools = new DebugTools(upstreams, _tools.AllTools().Select(t => $"{t.Name} ({t.ServerKey})").ToList(), hubs);
        }

        IReadOnlyList<string>? traceTail = null;
        if (trace is not null && traceLines > 0)
        {
            try
            {
                traceTail = await trace.TailAsync(traceLines).ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                traceTail = [$"(could not read the trace: {ex.Message})"];
            }
        }

        return new DebugReport(
            new DebugServer(version, Environment.OSVersion.ToString(), Environment.MachineName, _startedAt, DateTimeOffset.UtcNow,
                trace?.Path, _tools is not null, identities is not null, _tasks is not null),
            rooms, debugSessions, debugIdentities, debugTools, traceTail);
    }

    private static DebugAnnouncement Describe(AgentAnnouncePayload a) => new(
        a.Locality, a.Clearance, a.Skills, a.Description, a.CostTier, a.WantsDelegator, a.WorkMode, a.Persona, a.Sdk);

    // The bundle's shape. Records rather than dictionaries, so a field renamed here is renamed
    // in every bundle and a reader can be written against it.
    private sealed record DebugReport(
        DebugServer Server, IReadOnlyList<DebugRoom> Rooms, IReadOnlyList<DebugSession> Sessions,
        IReadOnlyList<DebugIdentity>? Identities, DebugTools? Tools, IReadOnlyList<string>? TraceTail);

    private sealed record DebugServer(
        string Version, string Os, string Machine, DateTimeOffset StartedAt, DateTimeOffset TakenAt,
        string? TraceFile, bool HasToolBackend, bool HasIdentityStore, bool HasTaskLedger);

    private sealed record DebugRoom(
        string Name, string? Topic, string? Parent, RoomDispatchMode Mode, RoomRegime Regime, RoomDisclosure Disclosure,
        RoomParley Parley, DataSensitivity Sensitivity, string? Delegator, bool DelegatorIsLocal,
        IReadOnlyList<string> Members, IReadOnlyList<DebugRoomAgent> Agents, IReadOnlyList<string> Leaving,
        DebugGuardrails Guardrails, IReadOnlyList<DebugAsk> OpenAsks, IReadOnlyList<DebugMessage> RecentMessages);

    private sealed record DebugRoomAgent(
        string Nick, AgentLocality Locality, DataSensitivity Clearance, IReadOnlyList<string> Skills, int CostTier,
        bool WantsDelegator, AgentWorkMode WorkMode, long JoinSequence);

    private sealed record DebugGuardrails(
        int ConsecutiveAgentMessages, bool LoopBroken, int RecentAgentMessages, int MaxConsecutive, int MaxPerMinute);

    private sealed record DebugAsk(string AskId, string Asker, IReadOnlyList<string> Questions);

    private sealed record DebugMessage(
        DateTimeOffset At, string Sender, string Text, string MessageId, string? FileId, string? ReplyTo,
        DateTimeOffset? EditedAt, DateTimeOffset? DeletedAt);

    private sealed record DebugSession(
        string Nick, bool IsAgent, bool IsAdmin, string Peer, DateTimeOffset? ConnectedAt, IReadOnlyList<string> Rooms,
        DebugAnnouncement? Requested, DebugAnnouncement? Effective, IReadOnlyList<string>? ToolGrants);

    private sealed record DebugAnnouncement(
        AgentLocality Locality, DataSensitivity Clearance, IReadOnlyList<string> Skills, string Description, int CostTier,
        bool WantsDelegator, AgentWorkMode WorkMode, string Persona, string Sdk);

    private sealed record DebugIdentity(
        string Nick, IReadOnlyList<string> Rooms, IReadOnlyList<string> Skills, AgentLocality Locality, DataSensitivity Clearance,
        int? CostTier, bool? WantsDelegator, AgentWorkMode? WorkMode, string Persona, string LastSdk, DateTimeOffset? LastSeenAt,
        bool Enrolled, bool EnrolmentPending);

    private sealed record DebugTools(
        IReadOnlyList<DebugUpstream>? Upstreams, IReadOnlyList<string> AllTools, IReadOnlyList<HubPayload> Hubs);

    private sealed record DebugUpstream(string Key, string State, string? LastError, int ToolCount);
}
