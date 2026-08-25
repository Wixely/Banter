using Banter.Client.Core;
using Banter.Protocol;
using Banter.Protocol.Transport;

namespace Banter.Agents.Sdk;

/// <summary>
/// Base class for a Banter agent: connect, join rooms, decide whether a message is for you, and
/// reply as a stream. Subclasses implement <see cref="RespondAsync"/> and nothing else.
///
/// <para>An agent is an ordinary Banter user whose account is flagged <c>IsAgent</c>, so the
/// server's per-room rate limit and loop-breaker already apply to it. The client-side rules here
/// are the polite layer on top: they stop an agent replying to itself or chattering into a room
/// nobody addressed, which is cheaper than being throttled for it.</para>
/// </summary>
public abstract class BanterAgent : IAsyncDisposable
{
    private BanterClient? _client;
    private readonly CancellationTokenSource _stopping = new();
    private readonly SemaphoreSlim _turnGate = new(1, 1);

    protected BanterAgent(BanterAgentOptions options) => Options = options;

    public BanterAgentOptions Options { get; }

    /// <summary>The connected client. Throws before <see cref="StartAsync"/>.</summary>
    protected BanterClient Client => _client ?? throw new InvalidOperationException("Agent is not started.");

    public string Nick => _client?.Nick ?? Options.User;

    /// <summary>Raised for every turn the agent takes, for logging or supervision.</summary>
    public event Action<string, string>? TurnStarted;

    /// <summary>Produce a reply to <paramref name="prompt"/> in <paramref name="room"/>.
    /// Yield the reply in pieces; each is streamed to the room as it arrives.</summary>
    protected abstract IAsyncEnumerable<string> RespondAsync(
        string room, string sender, string prompt, CancellationToken cancellationToken);

    /// <summary>Delegator nick per room, as last announced by the server. Null means none.</summary>
    private readonly Dictionary<string, string?> _delegators = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RoomDispatchMode> _modes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _roomStateLock = new();

    public async Task StartAsync(IBanterClientTransport transport, CancellationToken cancellationToken = default)
    {
        _client = await BanterClient.ConnectAsync(
            transport, Options.Server, Options.User, Options.Password,
            new BanterClientOptions { ClientName = Options.ClientName },
            cancellationToken).ConfigureAwait(false);

        _client.MessageReceived += OnMessage;
        _client.DelegatorChanged += OnDelegatorChanged;
        _client.RoomModeChanged += OnRoomModeChanged;
        _client.MemberJoined += OnMemberJoined;

        // Announce before joining, so the attributes are already on file when the server runs
        // the election our arrival triggers.
        await _client.AnnounceAgentAsync(
            new AgentAnnouncePayload(
                Options.User, Options.Locality, Options.Clearance, Options.Skills,
                Options.Description, Options.CostTier, Options.WantsDelegator),
            cancellationToken).ConfigureAwait(false);

        foreach (var room in Options.Rooms)
        {
            await _client.JoinAsync(room, cancellationToken).ConfigureAwait(false);
            await RefreshRosterAsync(room, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Re-read the roster whenever someone joins. Without this the roster is a snapshot taken at
    /// our own join: an agent arriving later would look like a human forever, and a delegator
    /// would answer its chatter — exactly the agent-to-agent loop the guardrails exist to stop.
    /// </summary>
    private void OnMemberJoined(JoinPayload j) =>
        _ = Task.Run(async () =>
        {
            try
            {
                await RefreshRosterAsync(j.Room, _stopping.Token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Best effort: a stale roster costs an unnecessary reply, not correctness.
            }
        });

    private void OnDelegatorChanged(RoomDelegatorPayload p)
    {
        lock (_roomStateLock)
        {
            _delegators[p.Room] = p.Nick;
        }
    }

    private void OnRoomModeChanged(RoomModePayload p)
    {
        lock (_roomStateLock)
        {
            _modes[p.Room] = p.Mode;
        }
    }

    /// <summary>True when this agent is the elected delegator for the room.</summary>
    public bool IsDelegatorFor(string room)
    {
        lock (_roomStateLock)
        {
            return _delegators.TryGetValue(room, out var nick)
                && string.Equals(nick, Nick, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>Current delegator for a room, or null.</summary>
    protected string? DelegatorFor(string room)
    {
        lock (_roomStateLock)
        {
            return _delegators.GetValueOrDefault(room);
        }
    }

    /// <summary>
    /// Dispatch mode for a room. Defaults to <see cref="RoomDispatchMode.Delegated"/> for a room
    /// we have not been told about — the quieter assumption, so an agent cannot start answering
    /// everything because a mode message was missed.
    /// </summary>
    protected RoomDispatchMode ModeFor(string room)
    {
        lock (_roomStateLock)
        {
            return _modes.GetValueOrDefault(room, RoomDispatchMode.Delegated);
        }
    }

    /// <summary>
    /// Say something into a room without being asked. This is how a delegator hands work over —
    /// naming the chosen agent in the room, so the routing decision is visible to the humans in it
    /// rather than happening on a side channel.
    /// </summary>
    public Task SayAsync(string room, string text, CancellationToken cancellationToken = default) =>
        Client.SendMessageAsync(room, text, cancellationToken).AsTask();

    /// <summary>Blocks until cancelled or <see cref="DisposeAsync"/>.</summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopping.Token);
        try
        {
            await Task.Delay(Timeout.Infinite, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private void OnMessage(MsgPayload m)
    {
        if (!ShouldRespond(m))
        {
            return;
        }

        // Handlers run on the receive loop; a turn can take many seconds, so it must not block it.
        _ = Task.Run(() => TakeTurnAsync(m));
    }

    /// <summary>
    /// Whether this message deserves a reply (PLAN §8a).
    ///
    /// <para>Never to itself — the one rule that turns a room into an infinite loop — and never to
    /// the server's own announcements, which would fight the loop-breaker.</para>
    ///
    /// <para>In a <b>delegated</b> room only the delegator acts on human messages; every other
    /// agent stays quiet until the delegator hands work to it by name. That hand-off is the only
    /// way a non-delegator speaks, which is what stops five agents answering one question.</para>
    ///
    /// <para>In a <b>mention</b> room every agent answers when named.</para>
    /// </summary>
    protected virtual bool ShouldRespond(MsgPayload m)
    {
        if (string.Equals(m.Sender, Nick, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (m.Sender == AgentGuardrailNames.System)
        {
            return false;
        }

        if (ModeFor(m.Room) == RoomDispatchMode.Delegated)
        {
            var delegatorNick = DelegatorFor(m.Room);

            // No delegator elected: the room falls back to mention behaviour rather than going
            // silent. A room with only frontier agents elects nobody, and it should still work.
            if (delegatorNick is null)
            {
                return Addressed(m);
            }

            return IsDelegatorFor(m.Room)
                ? !IsAgentSender(m)          // the delegator acts on human traffic
                : string.Equals(m.Sender, delegatorNick, StringComparison.OrdinalIgnoreCase)
                  && Addressed(m);           // everyone else waits to be handed work
        }

        return Options.RespondToEveryMessage || Addressed(m);
    }

    private bool Addressed(MsgPayload m) => m.Text.Contains(Nick, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether a message came from another agent. Known agents are those the roster reports, so
    /// this is best-effort: an unrostered sender is treated as human, which risks an extra reply
    /// rather than a silent room.
    /// </summary>
    private bool IsAgentSender(MsgPayload m)
    {
        lock (_roomStateLock)
        {
            return _knownAgents.Contains(m.Sender);
        }
    }

    private readonly HashSet<string> _knownAgents = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Refresh the set of agent nicks for a room, so the delegator can tell a human message from
    /// another agent's. Called after joins and whenever the delegator changes.
    /// </summary>
    protected async Task RefreshRosterAsync(string room, CancellationToken cancellationToken = default)
    {
        var roster = await Client.GetAgentsAsync(room, cancellationToken).ConfigureAwait(false);
        lock (_roomStateLock)
        {
            foreach (var agent in roster.Agents)
            {
                _knownAgents.Add(agent.Nick);
            }
        }
    }

    /// <summary>The agents currently known in any joined room, excluding this one.</summary>
    protected IReadOnlyCollection<string> KnownAgents
    {
        get
        {
            lock (_roomStateLock)
            {
                return _knownAgents.Where(n => !string.Equals(n, Nick, StringComparison.OrdinalIgnoreCase)).ToList();
            }
        }
    }

    private async Task TakeTurnAsync(MsgPayload m)
    {
        // One turn at a time. Two overlapping streams from one agent interleave in the room and
        // make both unreadable, and the second would usually be answering stale context anyway.
        if (!await _turnGate.WaitAsync(TimeSpan.Zero).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            TurnStarted?.Invoke(m.Room, m.Sender);
            var prompt = StripAddress(m.Text);

            await using var stream = await Client.StartMessageStreamAsync(m.Room, _stopping.Token)
                .ConfigureAwait(false);

            await foreach (var piece in RespondAsync(m.Room, m.Sender, prompt, _stopping.Token)
                .WithCancellation(_stopping.Token).ConfigureAwait(false))
            {
                if (piece.Length > 0)
                {
                    await stream.AppendAsync(piece, _stopping.Token).ConfigureAwait(false);
                }
            }

            await stream.CompleteAsync(cancellationToken: _stopping.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Shutting down mid-turn; the server closes the orphaned stream from what it received.
        }
        catch (Exception ex)
        {
            // A failed turn must not kill the agent — it stays in the room and answers the next
            // message. Reporting in-room is what makes a broken endpoint visible to humans.
            try
            {
                await Client.SendMessageAsync(m.Room, $"({Nick} failed to answer: {ex.Message})").ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The room is unreachable too; nothing useful left to do.
            }
        }
        finally
        {
            _turnGate.Release();
        }
    }

    /// <summary>Remove a leading mention so the model sees the question, not the addressing.</summary>
    protected string StripAddress(string text)
    {
        var trimmed = text.TrimStart();
        if (trimmed.StartsWith(Nick, StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[Nick.Length..].TrimStart(' ', ':', ',', '-');
        }

        return trimmed.Length > 0 ? trimmed : text;
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        if (_client is not null)
        {
            _client.MessageReceived -= OnMessage;
            _client.DelegatorChanged -= OnDelegatorChanged;
            _client.RoomModeChanged -= OnRoomModeChanged;
            _client.MemberJoined -= OnMemberJoined;
            await _client.DisposeAsync().ConfigureAwait(false);
        }

        _stopping.Dispose();
        _turnGate.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>Names the server uses for its own announcements.</summary>
public static class AgentGuardrailNames
{
    /// <summary>Matches <c>AgentGuardrails.SystemNick</c> — guardrail announcements come from
    /// this nick, and an agent replying to them would fight the loop-breaker.</summary>
    public const string System = "banter";
}
