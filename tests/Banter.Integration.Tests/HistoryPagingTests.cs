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
/// A history page has to fit in one frame, and how big it is was never ours to choose.
///
/// <para>The request carries a COUNT, which says nothing about bytes: the server clamps it to 500
/// and then builds a page out of whatever happens to be in the room. On a conduit the ceiling is
/// far below Banter's own 4 MB — a hundred long agent replies pass 192 KiB — and the send is
/// refused at the transport, so the reply never arrives and the request is left to time out. The
/// caller sees a hang, not a limit (PLAN §9, Phase 2.5).</para>
///
/// <para>The connection here reports a small frame and refuses an oversized one exactly as
/// <c>ShrineConnection</c> does, so these fail the way the real path fails rather than politely.
/// Messages go into the store directly: that is also the realistic shape of the problem, since a
/// message written over a wide path is read back over whichever path asks for it.</para>
/// </summary>
public sealed class HistoryPagingTests(ITestOutputHelper output) : IAsyncLifetime
{
    /// <summary>Small enough that a handful of messages passes it, large enough that the
    /// handshake and an ordinary reply do not.</summary>
    private const int Ceiling = 8 * 1024;

    private readonly TcpBanterTransport _transport = new();
    private readonly InMemoryAccountStore _accounts = new InMemoryAccountStore().AddUser("alice", "pw");

    private string _root = null!;
    private BanterDatabase _database = null!;
    private DbServerStore _store = null!;
    private BanterServer _server = null!;

    public async Task InitializeAsync()
    {
        _root = Path.Combine(Path.GetTempPath(), $"banter-paging-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _database = new BanterDatabase(BanterStorageOptions.DefaultSqlite(Path.Combine(_root, "banter.db")));
        await _database.InitializeAsync();
        _store = new DbServerStore(_database);
        var files = new FileStore(_database, new FileStoreOptions { DataDirectory = Path.Combine(_root, "files") });
        _server = new BanterServer(new NarrowTransport(_transport, Ceiling), _accounts, _store, files);
        await _server.StartAsync(new Uri("tcp://127.0.0.1:0"));
    }

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        BanterDatabase.ClearSqlitePools();
        Directory.Delete(_root, recursive: true);
    }

    /// <summary>Writes straight to the store, so the page under test is not also a test of
    /// broadcast. Returns the message ids in the order they were written.</summary>
    private async Task<string[]> FillAsync(string room, int count, int textBytes)
    {
        var ids = new string[count];
        for (var i = 0; i < count; i++)
        {
            ids[i] = $"m{i:D4}";
            await _store.AppendMessageAsync(new ChatMessage(
                ids[i], room, "alice", new string((char)('a' + (i % 26)), textBytes),
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), FileId: null));
        }

        return ids;
    }

    [Fact]
    public async Task APageTooBigForTheFrameComesBackTrimmed()
    {
        await using var client = await BanterClient.ConnectAsync(_transport, _server.Endpoint, "alice", "pw");
        await client.JoinAsync("#big");
        var written = await FillAsync("#big", count: 40, textBytes: 1024);

        // Ask for all of them at once: ~40 KB of text into an 8 KB frame.
        var page = await client.GetHistoryAsync("#big", limit: 40);
        output.WriteLine($"asked for 40, got {page.Messages.Count}, cursor {page.NextCursor ?? "(none)"}");

        Assert.NotEmpty(page.Messages);
        Assert.True(page.Messages.Count < 40, "the page was not trimmed");

        // The NEWEST survive: history is read backwards, so the tail is the part the caller
        // asked to see first.
        Assert.Equal(written[^1], page.Messages[^1].MessageId);

        // And the cursor points at the oldest one that did arrive, so the next request picks up
        // exactly what was dropped.
        Assert.Equal(page.Messages[0].MessageId, page.NextCursor);
    }

    /// <summary>The trim must not lose anything — only move it into the next request.</summary>
    [Fact]
    public async Task PagingBackwardsRecoversEveryMessage()
    {
        await using var client = await BanterClient.ConnectAsync(_transport, _server.Endpoint, "alice", "pw");
        await client.JoinAsync("#big");
        var written = await FillAsync("#big", count: 40, textBytes: 1024);

        var seen = new List<string>();
        string? cursor = null;
        for (var request = 0; request < 40; request++)
        {
            var page = await client.GetHistoryAsync("#big", beforeMessageId: cursor, limit: 40);
            seen.InsertRange(0, page.Messages.Select(m => m.MessageId!));
            cursor = page.NextCursor;
            if (cursor is null)
            {
                break;
            }
        }

        output.WriteLine($"recovered {seen.Count} messages in pages");
        Assert.Equal(written, seen);
    }

    /// <summary>
    /// One message on its own over the ceiling. There is nothing to trim towards, and it is not a
    /// cursor problem, so it is named rather than left to be refused at the transport — where the
    /// caller would have seen a hang.
    /// </summary>
    [Fact]
    public async Task OneMessageOverTheCeilingIsNamedRatherThanHung()
    {
        await using var client = await BanterClient.ConnectAsync(_transport, _server.Endpoint, "alice", "pw");
        await client.JoinAsync("#big");
        await FillAsync("#big", count: 1, textBytes: Ceiling * 2);

        var failed = await Assert.ThrowsAsync<BanterErrorException>(
            () => client.GetHistoryAsync("#big", limit: 10));

        Assert.Equal("PAGE_TOO_LARGE", failed.Code);
    }

    /// <summary>A server transport whose accepted connections carry a small frame and refuse an
    /// oversized one, the way a conduit does.</summary>
    private sealed class NarrowTransport(IBanterServerTransport inner, int maxFrameBytes) : IBanterServerTransport
    {
        public async Task<IBanterListener> ListenAsync(Uri endpoint, CancellationToken cancellationToken = default) =>
            new NarrowListener(await inner.ListenAsync(endpoint, cancellationToken), maxFrameBytes);

        private sealed class NarrowListener(IBanterListener inner, int max) : IBanterListener
        {
            public Uri LocalEndpoint => inner.LocalEndpoint;

            public async Task<IBanterConnection> AcceptAsync(CancellationToken cancellationToken = default) =>
                new NarrowConnection(await inner.AcceptAsync(cancellationToken), max);

            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }

        private sealed class NarrowConnection(IBanterConnection inner, int max) : IBanterConnection
        {
            public string RemoteDescription => inner.RemoteDescription;

            public int MaxFrameBytes => max;

            public ValueTask SendFrameAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken = default)
            {
                if (frame.Length > max)
                {
                    throw new InvalidOperationException(
                        $"A {frame.Length}-byte frame exceeds this conduit's {max}-byte limit.");
                }

                return inner.SendFrameAsync(frame, cancellationToken);
            }

            public ValueTask<byte[]?> ReceiveFrameAsync(CancellationToken cancellationToken = default) =>
                inner.ReceiveFrameAsync(cancellationToken);

            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }
}
