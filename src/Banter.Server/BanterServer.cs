using System.Collections.Concurrent;
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
    Persistence.IServerStore store) : IAsyncDisposable
{
    private readonly BanterCodec _codec = new();
    private readonly RoomEngine _engine = new(store);
    private readonly CancellationTokenSource _stopping = new();
    private readonly ConcurrentDictionary<Task, byte> _sessionTasks = new();
    private IBanterListener? _listener;
    private Task? _acceptLoop;
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

            var session = new ClientSession(connection, _codec, accounts, _engine);
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

        await Task.WhenAll(_sessionTasks.Keys).ConfigureAwait(false);
        await _engine.StopAsync().ConfigureAwait(false);
        _stopping.Dispose();
    }
}
