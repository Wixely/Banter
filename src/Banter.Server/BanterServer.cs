using System.Collections.Concurrent;
using System.Threading.Channels;
using Banter.Core;
using Banter.Protocol;
using Banter.Protocol.Transport;

namespace Banter.Server;

/// <summary>
/// The hub: accepts connections from any <see cref="IBanterServerTransport"/>, runs one
/// <see cref="ClientSession"/> per peer over a shared <see cref="RoomEngine"/>. Hostable
/// in-process for tests and from Program for real.
/// </summary>
public sealed class BanterServer(
    IBanterServerTransport transport,
    IAccountStore accounts,
    Persistence.IServerStore store,
    Files.FileStore files,
    AgentGuardrails? guardrails = null,
    Persistence.TaskStore? tasks = null,
    TaskLimits? taskLimits = null,
    Tools.IToolBroker? tools = null,
    IAgentIdentityStore? identities = null,
    IAccountAdminStore? accountAdmin = null,
    // Last, and optional, because a relic is a capability of one transport rather than of the
    // protocol: without it every file still transfers, over FILE_GET as it always has.
    Files.FileRelics? relics = null,
    PresenceLimits? presence = null) : IAsyncDisposable
{
    private readonly BanterCodec _codec = new();
    private readonly TaskLimits _taskLimits = taskLimits ?? TaskLimits.Default;
    private readonly PresenceLimits _presence = presence ?? PresenceLimits.Default;
    // The identity store reaches the engine so an announcement can be clamped to what the admin
    // decided: the machine running an agent is not the authority on how much the room trusts it.
    private readonly RoomEngine _engine = new(store, guardrails, tasks, taskLimits, tools, identities, presence);
    private readonly CancellationTokenSource _stopping = new();
    private readonly ConcurrentDictionary<Task, byte> _sessionTasks = new();
    private IBanterListener? _listener;
    private Task? _acceptLoop;
    private Task? _leaseSweep;
    private Task? _uploadSweep;
    private Task? _presenceSweep;
    private readonly bool _tasksEnabled = tasks is not null;
    private bool _disposed;

    public Uri Endpoint => _listener?.LocalEndpoint
        ?? throw new InvalidOperationException("The server has not been started.");

    public async Task StartAsync(Uri endpoint, CancellationToken cancellationToken = default)
    {
        if (_listener is not null)
        {
            throw new InvalidOperationException("The server is already started.");
        }

        _listener = await transport.ListenAsync(endpoint, cancellationToken).ConfigureAwait(false);
        await _engine.StartAsync(cancellationToken).ConfigureAwait(false);
        _acceptLoop = Task.Run(AcceptLoopAsync, CancellationToken.None);

        if (_tasksEnabled)
        {
            _leaseSweep = Task.Run(LeaseSweepAsync, CancellationToken.None);
        }

        // Unconditional, unlike the lease sweep: abandoned uploads hold an open file handle whether
        // or not this deployment uses the work ledger.
        _uploadSweep = Task.Run(UploadSweepAsync, CancellationToken.None);

        // Only when there is a grace to run out. At zero the engine announces a departure as it
        // happens, so there is nothing for a timer to come back for.
        if (_presence.ReconnectGrace > TimeSpan.Zero)
        {
            _presenceSweep = Task.Run(PresenceSweepAsync, CancellationToken.None);
        }
    }

    /// <summary>
    /// Periodically gives up on uploads nobody is feeding. The timer lives here because the server
    /// owns the lifecycle; what counts as abandoned, and what clearing it up means, belongs to the
    /// file store.
    /// </summary>
    private async Task UploadSweepAsync()
    {
        // A tenth of the lifetime, so an upload is reclaimed reasonably promptly after it lapses
        // rather than up to a whole lifetime late, and floored so a short lifetime in a test cannot
        // turn this into a spin.
        var every = TimeSpan.FromMilliseconds(
            Math.Max(250, files.AbandonedUploadLifetime.TotalMilliseconds / 10));

        using var timer = new PeriodicTimer(every);
        try
        {
            while (await timer.WaitForNextTickAsync(_stopping.Token).ConfigureAwait(false))
            {
                await files.SweepAbandonedUploadsAsync(_stopping.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        catch (Exception)
        {
            // A sweep that cannot reach its database must not take the server down with it. The cost
            // of losing one is a handle held until the next tick.
        }
    }

    /// <summary>
    /// Periodically announces the departures whose grace has run out. Like the lease sweep, it
    /// only queues work onto the room engine, so the announcement is ordered against joins rather
    /// than racing one.
    /// </summary>
    private async Task PresenceSweepAsync()
    {
        using var timer = new PeriodicTimer(_presence.SweepInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(_stopping.Token).ConfigureAwait(false))
            {
                await _engine.SweepDeparturesAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        catch (ChannelClosedException)
        {
            // Engine stopped first; nothing left to sweep into.
        }
    }

    /// <summary>
    /// Periodically reclaims tasks whose lease lapsed. The sweep only queues work onto the room
    /// engine, so the actual reclaim is ordered against claims like every other mutation.
    /// </summary>
    private async Task LeaseSweepAsync()
    {
        using var timer = new PeriodicTimer(_taskLimits.SweepInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(_stopping.Token).ConfigureAwait(false))
            {
                await _engine.SweepExpiredTasksAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        catch (ChannelClosedException)
        {
            // Engine stopped first; nothing left to sweep into.
        }
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            IBanterConnection connection;
            try
            {
                connection = await _listener!.AcceptAsync(_stopping.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                return;
            }

            var session = new ClientSession(
                connection, _codec, accounts, _engine, files, relics, identities, accountAdmin);
            var run = session.RunAsync(_stopping.Token);
            _sessionTasks.TryAdd(run, 0);
            _ = run.ContinueWith(t => _sessionTasks.TryRemove(t, out _), TaskScheduler.Default);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _stopping.CancelAsync().ConfigureAwait(false);
        if (_listener is not null)
        {
            await _listener.DisposeAsync().ConfigureAwait(false);
        }

        if (_acceptLoop is not null)
        {
            await _acceptLoop.ConfigureAwait(false);
        }

        if (_uploadSweep is not null)
        {
            await _uploadSweep.ConfigureAwait(false);
        }

        if (_leaseSweep is not null)
        {
            await _leaseSweep.ConfigureAwait(false);
        }

        if (_presenceSweep is not null)
        {
            await _presenceSweep.ConfigureAwait(false);
        }

        await Task.WhenAll(_sessionTasks.Keys).ConfigureAwait(false);
        await _engine.StopAsync().ConfigureAwait(false);
        _stopping.Dispose();
    }
}
