using Banter.Client.Core;
using Banter.Core;
using Banter.Protocol.Transport;
using Banter.Server;
using Banter.Server.Files;
using Banter.Server.Persistence;
using Xunit;
using Xunit.Abstractions;

namespace Banter.Integration.Tests;

/// <summary>
/// The client speaks when nobody is speaking to it.
///
/// <para>Chat is mostly silence, and a CupriNet node closes a Pilgrimage that has gone quiet —
/// measured at the default five minutes, and not configurable through Nodestar, so a keepalive is
/// the only lever we have. It is also what IRC has always done, and for the second reason too: a
/// connection that is never used is indistinguishable from one that has died until somebody tries
/// to type into it.</para>
///
/// <para>What is pinned here is that the client emits traffic while idle, and stops when told to.
/// The node's own timer is not exercised — five minutes is too long for a suite, and it was
/// measured separately against a live node, where moving <c>PilgrimageIdleTimeout</c> to 15s and
/// 45s ended the session at 15.0s and 45.0s.</para>
/// </summary>
public sealed class KeepAliveTests(ITestOutputHelper output) : IAsyncLifetime
{
    private readonly TcpBanterTransport _transport = new();
    private readonly InMemoryAccountStore _accounts = new InMemoryAccountStore().AddUser("alice", "pw");

    private string _root = null!;
    private BanterDatabase _database = null!;
    private BanterServer _server = null!;

    /// <summary>Counts what the client puts on the wire, so "quiet" can be measured.</summary>
    private sealed class CountingTransport(IBanterClientTransport inner) : IBanterClientTransport
    {
        private int _frames;

        public int FramesSent => Volatile.Read(ref _frames);

        public async Task<IBanterConnection> ConnectAsync(Uri endpoint, CancellationToken cancellationToken = default) =>
            new Counted(await inner.ConnectAsync(endpoint, cancellationToken), this);

        private sealed class Counted(IBanterConnection inner, CountingTransport owner) : IBanterConnection
        {
            public string RemoteDescription => inner.RemoteDescription;

            public ValueTask SendFrameAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken = default)
            {
                Interlocked.Increment(ref owner._frames);
                return inner.SendFrameAsync(frame, cancellationToken);
            }

            public ValueTask<byte[]?> ReceiveFrameAsync(CancellationToken cancellationToken = default) =>
                inner.ReceiveFrameAsync(cancellationToken);

            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }

    public async Task InitializeAsync()
    {
        _root = Path.Combine(Path.GetTempPath(), $"banter-keepalive-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _database = new BanterDatabase(BanterStorageOptions.DefaultSqlite(Path.Combine(_root, "banter.db")));
        await _database.InitializeAsync();
        var files = new FileStore(_database, new FileStoreOptions { DataDirectory = Path.Combine(_root, "files") });
        _server = new BanterServer(_transport, _accounts, new DbServerStore(_database), files);
        await _server.StartAsync(new Uri("tcp://127.0.0.1:0"));
    }

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        BanterDatabase.ClearSqlitePools();
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task AnIdleClientKeepsTalking()
    {
        var counting = new CountingTransport(_transport);
        await using var client = await BanterClient.ConnectAsync(
            counting, _server.Endpoint, "alice", "pw",
            new BanterClientOptions { KeepAliveInterval = TimeSpan.FromMilliseconds(200) });

        var afterHandshake = counting.FramesSent;
        await Task.Delay(1200);
        var afterIdling = counting.FramesSent;

        output.WriteLine($"frames: {afterHandshake} after connecting, {afterIdling} after idling 1.2s");

        // Several ticks' worth, and nobody asked for any of them.
        Assert.True(
            afterIdling >= afterHandshake + 3,
            $"an idle client sent only {afterIdling - afterHandshake} frames in 1.2s of a 200ms keepalive");

        // Still usable: the pings are not consuming replies meant for anyone else.
        await client.JoinAsync("#quiet");
        await client.SendMessageAsync("#quiet", "still here");
        Assert.Equal("still here", (await client.GetHistoryAsync("#quiet", limit: 5)).Messages[^1].Text);
    }

    [Fact]
    public async Task TurningItOffMeansSilence()
    {
        var counting = new CountingTransport(_transport);
        await using var client = await BanterClient.ConnectAsync(
            counting, _server.Endpoint, "alice", "pw",
            new BanterClientOptions { KeepAliveInterval = TimeSpan.Zero });

        var afterHandshake = counting.FramesSent;
        await Task.Delay(1200);

        // A head that has its own idea about pinging, or a test that wants a quiet wire, can say so
        // — and then nothing at all goes out.
        Assert.Equal(afterHandshake, counting.FramesSent);
    }
}
