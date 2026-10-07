using Banter.App;
using Banter.Client.Core;
using Banter.Core;
using Banter.Protocol;
using Banter.Protocol.Transport;
using Banter.Server;
using Banter.Server.Files;
using Banter.Server.Persistence;
using Xunit;
using Xunit.Abstractions;

namespace Banter.Integration.Tests;

/// <summary>
/// Reading further back works more than once.
///
/// <para>The bug, reported from a phone: the first "earlier messages" worked and every one after
/// it did nothing. The cause is a seam rather than either side of it. Every view-model mutation is
/// queued and applied on the render thread, so the cursor a reply carries is not visible until a
/// frame has run - and a tap handler runs BEFORE the frame that drains the queue. On a host whose
/// pump is driven by input, which is what a phone is, the second request therefore asked for the
/// page it already had, got the same messages, discarded them all as duplicates and prepended
/// nothing.</para>
///
/// <para>These drive the session WITHOUT draining the view-model between calls, which is what
/// reproduces it: on a desktop the render loop hides the whole thing by running a frame in
/// between, which is why it was only ever seen on the phone.</para>
/// </summary>
public sealed class ScrollbackPagingTests(ITestOutputHelper output) : IAsyncLifetime
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private readonly TcpBanterTransport _transport = new();
    private readonly InMemoryAccountStore _accounts = new InMemoryAccountStore().AddUser("alice", "pw");

    private string _root = null!;
    private BanterDatabase _database = null!;
    private DbServerStore _store = null!;
    private BanterServer _server = null!;

    public async Task InitializeAsync()
    {
        _root = Path.Combine(Path.GetTempPath(), $"banter-scrollback-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _database = new BanterDatabase(BanterStorageOptions.DefaultSqlite(Path.Combine(_root, "banter.db")));
        await _database.InitializeAsync();
        _store = new DbServerStore(_database);

        var files = new FileStore(_database, new FileStoreOptions { DataDirectory = Path.Combine(_root, "files") });
        _server = new BanterServer(_transport, _accounts, _store, files);
        await _server.StartAsync(new Uri("tcp://127.0.0.1:0"));

        for (var i = 0; i < 60; i++)
        {
            await _store.AppendMessageAsync(new ChatMessage(
                $"m{i:D4}", "#main", "bob", $"message {i}",
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), FileId: null));
        }
    }

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        BanterDatabase.ClearSqlitePools();
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task EveryPageBackIsANewPage()
    {
        await using var client = await BanterClient.ConnectAsync(_transport, _server.Endpoint, "alice", "pw");
        var vm = new ChatViewModel();
        vm.SetNick("alice");
        var session = new BanterChatSession(client, vm);

        await session.JoinAsync("#main", history: 10).WaitAsync(Patience);
        vm.ApplyPending();
        vm.SwitchTo("#main");
        var afterJoin = vm.Model.Messages.Count(m => m.Id.Length > 0);
        output.WriteLine($"joined with {afterJoin} message(s)");

        // Three pages with NO frame between them, which is the phone's order of events.
        for (var page = 1; page <= 3; page++)
        {
            await session.LoadOlderAsync("#main", limit: 10).WaitAsync(Patience);
        }

        vm.ApplyPending();
        var total = vm.Model.Messages.Count(m => m.Id.Length > 0);
        output.WriteLine($"after three pages: {total} message(s)");

        // Ten per page, and every page new: anything less means a page was asked for twice and
        // thrown away as duplicates, which is exactly what "it does nothing" looked like.
        Assert.Equal(afterJoin + 30, total);

        // In order, oldest first, with nothing repeated.
        var ids = vm.Model.Messages.Select(m => m.Id).Where(id => id.Length > 0).ToList();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(ids.OrderBy(id => id, StringComparer.Ordinal), ids);
    }

    /// <summary>
    /// And it still stops at the beginning. A cursor the session keeps for itself must run out
    /// where the history does, or "earlier messages" would go on offering a page that is not there.
    /// </summary>
    [Fact]
    public async Task ItRunsOutAtTheStartOfTheRoom()
    {
        await using var client = await BanterClient.ConnectAsync(_transport, _server.Endpoint, "alice", "pw");
        var vm = new ChatViewModel();
        vm.SetNick("alice");
        var session = new BanterChatSession(client, vm);

        await session.JoinAsync("#main", history: 10).WaitAsync(Patience);
        vm.ApplyPending();
        vm.SwitchTo("#main");

        // More pages than there is history.
        for (var page = 0; page < 12; page++)
        {
            await session.LoadOlderAsync("#main", limit: 10).WaitAsync(Patience);
        }

        vm.ApplyPending();

        // Counted by id: the room also carries local system lines ("you joined"), which are the
        // client's own and have none.
        var fromTheServer = vm.Model.Messages.Count(m => m.Id.Length > 0);
        output.WriteLine($"{fromTheServer} stored message(s) of {vm.Model.Messages.Count} rows, "
            + $"canLoadOlder={vm.CanLoadOlder("#main")}");

        Assert.Equal(60, fromTheServer);
        Assert.False(vm.CanLoadOlder("#main"), "the control should be gone at the start of the room");
    }
}
