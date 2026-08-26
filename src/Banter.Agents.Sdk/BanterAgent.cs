using Banter.Client.Core;
using Banter.Core;
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
public abstract partial class BanterAgent : IAsyncDisposable
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
        _client.TaskChanged += OnTaskChanged;

        // Announce before joining, so the attributes are already on file when the server runs
        // the election our arrival triggers.
        await _client.AnnounceAgentAsync(
            new AgentAnnouncePayload(
                Options.User, Options.Locality, Options.Clearance, Options.Skills,
                Options.Description, Options.CostTier, Options.WantsDelegator),
            cancellationToken).ConfigureAwait(false);

        // Before joining anything, so the first message in a room can already use tools.
        await RefreshToolsAsync(cancellationToken).ConfigureAwait(false);

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
    /// Open a child room, bring the chosen agents into it, and put the request there. Returns
    /// false if any step fails, so the caller can fall back to answering in the main room.
    ///
    /// <para>The sub-room inherits the parent's sensitivity server-side, and moving an agent that
    /// is not cleared for it is refused — so this cannot become a way to continue a sensitive
    /// conversation somewhere a frontier agent is eligible.</para>
    /// </summary>
    private async Task<bool> TryOpenSubRoomAsync(string parent, string prompt, IReadOnlyList<string> agents)
    {
        var name = SubRoomName(parent, prompt);
        try
        {
            await Client.CreateSubRoomAsync(name, parent, Summarise(prompt), _stopping.Token).ConfigureAwait(false);

            foreach (var agent in agents)
            {
                await Client.MoveAgentAsync(agent, name, "working this together", _stopping.Token)
                    .ConfigureAwait(false);
            }

            await SayAsync(parent, $"Taking this to {name} with {string.Join(", ", agents)}.").ConfigureAwait(false);
            await SayAsync(name, $"{string.Join(", ", agents)}: {prompt}").ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            await SayAsync(parent, $"(could not open a side room: {ex.Message}; continuing here)")
                .ConfigureAwait(false);
            return false;
        }
    }

    /// <summary>
    /// A room name derived from the work, so the room list reads as a list of things being done
    /// rather than a wall of identifiers. A short random suffix keeps two similar requests from
    /// colliding, but the readable part comes first.
    /// </summary>
    public static string SubRoomName(string parent, string prompt)
    {
        // Split on anything that is not a letter or digit, so a generated name can never pick up
        // a character the server would reject as an invalid room name.
        var words = new string(prompt.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ').ToArray())
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(w => w.Length > 2 && !Filler.Contains(w))
            .Take(4)
            .ToList();

        var slug = string.Join('-', words);
        if (slug.Length > 32)
        {
            slug = slug[..32].TrimEnd('-');
        }

        // Nothing usable in the request: fall back to the parent's name rather than an empty slug.
        var stem = slug.Length > 0 ? slug : parent.TrimStart('#');
        return $"#{stem}-{Guid.NewGuid().ToString("N")[..4]}";
    }

    /// <summary>Words that carry no meaning in a room name.</summary>
    private static readonly HashSet<string> Filler = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "a", "an", "and", "or", "but", "for", "with", "about", "into", "from", "this",
        "that", "these", "those", "can", "you", "your", "our", "please", "what", "does", "did",
        "how", "why", "when", "who", "everyone", "all", "think", "would", "could", "should",
        "here", "there", "some", "any", "get", "got", "let", "make", "need", "want", "have",
    };

    /// <summary>A room topic should be a label, not the whole request.</summary>
    private static string Summarise(string prompt)
    {
        var line = prompt.ReplaceLineEndings(" ").Trim();
        return line.Length <= 80 ? line : line[..80] + "…";
    }

    /// <summary>Whether a rostered agent is a frontier one, for naming recipients in an egress
    /// notice. Unknown locality counts as frontier, as everywhere else.</summary>
    private bool IsFrontier(string nick)
    {
        lock (_roomStateLock)
        {
            return _rosters.Values
                .SelectMany(r => r)
                .Any(a => string.Equals(a.Nick, nick, StringComparison.OrdinalIgnoreCase)
                          && a.Locality != AgentLocality.Local);
        }
    }

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
    /// Refresh the roster for a room: who the agents are, and the attributes a delegator routes
    /// on. Called after joins and whenever anyone joins afterwards.
    /// </summary>
    protected async Task RefreshRosterAsync(string room, CancellationToken cancellationToken = default)
    {
        var roster = await Client.GetAgentsAsync(room, cancellationToken).ConfigureAwait(false);
        lock (_roomStateLock)
        {
            var candidates = new List<AgentCandidate>(roster.Agents.Count);
            long order = 0;
            foreach (var agent in roster.Agents)
            {
                _knownAgents.Add(agent.Nick);
                candidates.Add(new AgentCandidate(
                    agent.Nick, agent.Locality, agent.Clearance, agent.Skills, agent.CostTier, order++));
            }

            _rosters[room] = candidates;
        }
    }

    /// <summary>Agents in a room with their routing attributes, as last read from the server.</summary>
    protected IReadOnlyList<AgentCandidate> RosterFor(string room)
    {
        lock (_roomStateLock)
        {
            return _rosters.TryGetValue(room, out var roster) ? roster : [];
        }
    }

    private readonly Dictionary<string, List<AgentCandidate>> _rosters =
        new(StringComparer.OrdinalIgnoreCase);

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

    /// <summary>
    /// As delegator, decide who should handle this and hand it over. Returns true when the
    /// request was routed elsewhere, so this agent should not also answer it.
    ///
    /// <para>The hand-off happens as an ordinary room message naming the chosen agent — that is
    /// what the receiving agent listens for, and it keeps the routing decision visible to the
    /// humans in the room rather than on a side channel.</para>
    /// </summary>
    private async Task<bool> TryRouteAsync(MsgPayload m)
    {
        if (Options.Routing is not { } routing || !IsDelegatorFor(m.Room))
        {
            return false;
        }

        var classification = await routing.Classifier.ClassifyAsync(StripAddress(m.Text), _stopping.Token)
            .ConfigureAwait(false);

        var roster = RosterFor(m.Room);
        var routingRequest = new RoutingRequest(
            classification.Sensitivity, classification.Skills, routing.AllowFrontier);

        var wantsEveryone = routing.FanOutPhrases.Any(
            phrase => m.Text.Contains(phrase, StringComparison.OrdinalIgnoreCase));

        var decision = wantsEveryone
            ? RequestRouting.ChooseAll(roster, routingRequest, excludeNick: Nick)
            : RequestRouting.Choose(roster, routingRequest, excludeNick: Nick);

        if (!decision.HasRecipients)
        {
            // Nobody better than us: answer it ourselves. Reporting why keeps a silent fallback
            // from looking like the routing simply did not run.
            if (routing.ExplainDecisions)
            {
                await SayAsync(m.Room, $"({decision.Reason}; handling this myself)").ConfigureAwait(false);
            }

            return false;
        }

        // Data leaving our systems is the most consequential thing this room does, so it is
        // announced before it happens, names every recipient, and is never folded into a
        // hand-off line where it could be skimmed past.
        if (decision.CrossesEgressBoundary)
        {
            var leaving = decision.Agents.Where(IsFrontier).ToList();
            await SayAsync(
                m.Room,
                $"[egress] sending this to {string.Join(", ", leaving)}, " +
                $"{(leaving.Count == 1 ? "which is a third-party agent" : "which are third-party agents")}. " +
                $"Classified {classification.Sensitivity.ToString().ToLowerInvariant()}: {classification.Rationale}.")
                .ConfigureAwait(false);
        }

        var prompt = StripAddress(m.Text);

        // A fan-out that involves a third party stays in the main room, whatever the sub-room
        // setting says. Two reasons, and they point the same way: the sub-room inherits the
        // parent's sensitivity, so a frontier agent could not be moved into it anyway; and
        // moving the one exchange that leaves our systems into a side channel would make the
        // most consequential thing in the room the least visible.
        if (routing.SubRoomForFanOut && decision.Agents.Count > 1 && !decision.CrossesEgressBoundary)
        {
            if (await TryOpenSubRoomAsync(m.Room, prompt, decision.Agents).ConfigureAwait(false))
            {
                return true;
            }

            // Falling through on failure is deliberate: the work still gets done in the main
            // room rather than being dropped because a side channel could not be opened.
        }

        foreach (var agent in decision.Agents)
        {
            await SayAsync(m.Room, $"{agent}: {prompt}").ConfigureAwait(false);
        }

        if (routing.ExplainDecisions && !decision.CrossesEgressBoundary)
        {
            await SayAsync(
                m.Room,
                $"(routed to {string.Join(", ", decision.Agents)} - {decision.Reason})").ConfigureAwait(false);
        }

        return true;
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
            // Routing first: if the work belongs to another agent, hand it over and stop. The
            // turn gate is still held, so a second message cannot start a competing hand-off.
            if (await TryRouteAsync(m).ConfigureAwait(false))
            {
                return;
            }

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
            _client.TaskChanged -= OnTaskChanged;
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
