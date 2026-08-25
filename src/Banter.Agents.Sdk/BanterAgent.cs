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

    public async Task StartAsync(IBanterClientTransport transport, CancellationToken cancellationToken = default)
    {
        _client = await BanterClient.ConnectAsync(
            transport, Options.Server, Options.User, Options.Password,
            new BanterClientOptions { ClientName = Options.ClientName },
            cancellationToken).ConfigureAwait(false);

        _client.MessageReceived += OnMessage;

        foreach (var room in Options.Rooms)
        {
            await _client.JoinAsync(room, cancellationToken).ConfigureAwait(false);
        }
    }

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
    /// Whether this message deserves a reply. Never to itself — that is the one rule that turns a
    /// room into an infinite loop — and, unless the room is configured otherwise, only when
    /// addressed by nick.
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

        if (Options.RespondToEveryMessage)
        {
            return true;
        }

        return m.Text.Contains(Nick, StringComparison.OrdinalIgnoreCase);
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
