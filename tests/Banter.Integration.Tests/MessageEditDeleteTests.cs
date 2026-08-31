using Banter.Client.Core;
using Banter.Core;
using Banter.Protocol;
using Banter.Protocol.Transport;
using Banter.Server;
using Banter.Server.Persistence;
using Xunit;

namespace Banter.Integration.Tests;

/// <summary>
/// Editing and deleting what has already been said, against a real server and a real database.
///
/// <para>The rules these pin are the point of the feature, and they are not symmetric: anyone may
/// take words away who is entitled to, but nobody may put different words in somebody else's
/// mouth.</para>
/// </summary>
public sealed class MessageEditDeleteTests : IAsyncLifetime
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private readonly TcpBanterTransport _transport = new();
    private readonly InMemoryAccountStore _accounts = new InMemoryAccountStore()
        .AddUser("alice", "pw")
        .AddUser("bob", "pw")
        .AddUser("admin", "pw", isAgent: false, isAdmin: true);

    private string _dbPath = null!;
    private string _dataDir = null!;
    private BanterDatabase _database = null!;
    private DbServerStore _store = null!;
    private Banter.Server.Files.FileStore _files = null!;
    private BanterServer _server = null!;

    public async Task InitializeAsync()
    {
        var id = Guid.NewGuid().ToString("N");
        _dbPath = Path.Combine(Path.GetTempPath(), $"banter-ed-{id}.db");
        _dataDir = Path.Combine(Path.GetTempPath(), $"banter-ed-files-{id}");
        _database = new BanterDatabase(BanterStorageOptions.DefaultSqlite(_dbPath));
        await _database.InitializeAsync();
        _store = new DbServerStore(_database);
        _files = new Banter.Server.Files.FileStore(_database, new Banter.Server.Files.FileStoreOptions { DataDirectory = _dataDir });
        _server = new BanterServer(_transport, _accounts, _store, _files);
        await _server.StartAsync(new Uri("tcp://127.0.0.1:0"));
    }

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        BanterDatabase.ClearSqlitePools();
        File.Delete(_dbPath);
        if (Directory.Exists(_dataDir))
        {
            Directory.Delete(_dataDir, recursive: true);
        }
    }

    private Task<BanterClient> ConnectAsync(string user) =>
        BanterClient.ConnectAsync(_transport, _server.Endpoint, user, "pw");

    /// <summary>Says something as <paramref name="who"/> and returns the id the server gave it.</summary>
    private static async Task<string> SayAsync(BanterClient who, string room, string text)
    {
        var said = new TaskCompletionSource<MsgPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnMessage(MsgPayload m)
        {
            if (m.Room == room && m.Text == text)
            {
                said.TrySetResult(m);
            }
        }

        who.MessageReceived += OnMessage;
        try
        {
            await who.SendMessageAsync(room, text);
            return (await said.Task.WaitAsync(Patience)).MessageId!;
        }
        finally
        {
            who.MessageReceived -= OnMessage;
        }
    }

    private static Task<T> Next<T>(Action<Action<T>> subscribe)
    {
        var source = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        subscribe(value => source.TrySetResult(value));
        return source.Task;
    }

    [Fact]
    public async Task AnAuthorEditsTheirOwnAndTheRoomSeesIt()
    {
        await using var alice = await ConnectAsync("alice");
        await using var bob = await ConnectAsync("bob");
        await alice.JoinAsync("#edit");
        await bob.JoinAsync("#edit");

        var id = await SayAsync(alice, "#edit", "teh cat sat");
        var seen = Next<EditPayload>(h => bob.MessageEdited += p => h(p));

        await alice.EditMessageAsync("#edit", id, "the cat sat");

        var edit = await seen.WaitAsync(Patience);
        Assert.Equal(id, edit.MessageId);
        Assert.Equal("the cat sat", edit.Text);
        // Named as alice's, and stamped, so a client can mark it edited rather than silently
        // swapping the words under a reader who already read them.
        Assert.Equal("alice", edit.Sender);
        Assert.True(edit.EditedAt > 0);
    }

    [Fact]
    public async Task NobodyElseMayEditIt()
    {
        await using var alice = await ConnectAsync("alice");
        await using var bob = await ConnectAsync("bob");
        await alice.JoinAsync("#edit");
        await bob.JoinAsync("#edit");

        var id = await SayAsync(alice, "#edit", "alice said this");
        var refused = Next<ErrorPayload>(h => bob.ServerError += p => h(p));

        await bob.EditMessageAsync("#edit", id, "bob put words here");

        Assert.Equal("NOT_YOURS", (await refused.WaitAsync(Patience)).Code);

        // And the words are untouched, which is the half that matters.
        var stored = await _store.GetMessageAsync("#edit", id);
        Assert.Equal("alice said this", stored!.Text);
    }

    [Fact]
    public async Task AnAdminMayNotEditSomebodyElsesEither()
    {
        await using var alice = await ConnectAsync("alice");
        await using var admin = await ConnectAsync("admin");
        await alice.JoinAsync("#edit");
        await admin.JoinAsync("#edit");

        var id = await SayAsync(alice, "#edit", "alice said this");
        var refused = Next<ErrorPayload>(h => admin.ServerError += p => h(p));

        await admin.EditMessageAsync("#edit", id, "the admin's version");

        // Being an operator is authority to remove, never to rewrite. A room where an admin can
        // change what somebody is recorded as saying is one where nothing read can be trusted.
        Assert.Equal("NOT_YOURS", (await refused.WaitAsync(Patience)).Code);
        Assert.Equal("alice said this", (await _store.GetMessageAsync("#edit", id))!.Text);
    }

    [Fact]
    public async Task AnAuthorDeletesTheirOwnAndTheWordsAreGone()
    {
        await using var alice = await ConnectAsync("alice");
        await using var bob = await ConnectAsync("bob");
        await alice.JoinAsync("#del");
        await bob.JoinAsync("#del");

        var id = await SayAsync(alice, "#del", "something regrettable");
        var seen = Next<DeletePayload>(h => bob.MessageDeleted += p => h(p));

        await alice.DeleteMessageAsync("#del", id);

        var delete = await seen.WaitAsync(Patience);
        Assert.Equal(id, delete.MessageId);
        Assert.Equal("alice", delete.Sender);

        // Actually gone from storage, not merely flagged: a delete that keeps the text has not
        // done what the person asking for it believed it did.
        var stored = await _store.GetMessageAsync("#del", id);
        Assert.Equal("", stored!.Text);
        Assert.NotNull(stored.DeletedAt);
    }

    [Fact]
    public async Task AnAdminMayDeleteAnyones()
    {
        await using var alice = await ConnectAsync("alice");
        await using var admin = await ConnectAsync("admin");
        await alice.JoinAsync("#del");
        await admin.JoinAsync("#del");

        var id = await SayAsync(alice, "#del", "moderate me");
        var seen = Next<DeletePayload>(h => alice.MessageDeleted += p => h(p));

        await admin.DeleteMessageAsync("#del", id);

        // The author is named, not the remover: the room is being told whose message went.
        Assert.Equal("alice", (await seen.WaitAsync(Patience)).Sender);
        Assert.Equal("", (await _store.GetMessageAsync("#del", id))!.Text);
    }

    [Fact]
    public async Task SomeoneWhoIsNeitherAuthorNorAdminMayNot()
    {
        await using var alice = await ConnectAsync("alice");
        await using var bob = await ConnectAsync("bob");
        await alice.JoinAsync("#del");
        await bob.JoinAsync("#del");

        var id = await SayAsync(alice, "#del", "not bob's to remove");
        var refused = Next<ErrorPayload>(h => bob.ServerError += p => h(p));

        await bob.DeleteMessageAsync("#del", id);

        Assert.Equal("NOT_YOURS", (await refused.WaitAsync(Patience)).Code);
        Assert.Equal("not bob's to remove", (await _store.GetMessageAsync("#del", id))!.Text);
    }

    [Fact]
    public async Task ADeletedMessageCannotBeEditedBackIntoExistence()
    {
        await using var alice = await ConnectAsync("alice");
        await alice.JoinAsync("#del");

        var id = await SayAsync(alice, "#del", "gone");
        await alice.DeleteMessageAsync("#del", id);
        await Task.Delay(200);

        var refused = Next<ErrorPayload>(h => alice.ServerError += p => h(p));
        await alice.EditMessageAsync("#del", id, "back again");

        Assert.Equal("NO_SUCH_MESSAGE", (await refused.WaitAsync(Patience)).Code);
        Assert.Equal("", (await _store.GetMessageAsync("#del", id))!.Text);
    }

    [Fact]
    public async Task HistoryReplaysTheEditAndTheDeletion()
    {
        await using var alice = await ConnectAsync("alice");
        await alice.JoinAsync("#replay");

        var edited = await SayAsync(alice, "#replay", "first draft");
        var removed = await SayAsync(alice, "#replay", "to be removed");
        await SayAsync(alice, "#replay", "untouched");

        await alice.EditMessageAsync("#replay", edited, "second draft");
        await alice.DeleteMessageAsync("#replay", removed);
        await Task.Delay(300);

        // A fresh client sees the room as it stands, not as it was typed.
        await using var bob = await ConnectAsync("bob");
        await bob.JoinAsync("#replay");
        var page = await bob.GetHistoryAsync("#replay", limit: 50).WaitAsync(Patience);

        var replayedEdit = page.Messages.Single(m => m.MessageId == edited);
        Assert.Equal("second draft", replayedEdit.Text);
        Assert.True(replayedEdit.EditedAt > 0, "a reconnect must not lose the edited marker");

        var replayedDelete = page.Messages.Single(m => m.MessageId == removed);
        Assert.Equal("", replayedDelete.Text);
        Assert.True(replayedDelete.DeletedAt > 0, "otherwise a deleted message replays as a blank line");
    }

    [Fact]
    public async Task AnEmptyEditIsRefusedRatherThanTreatedAsADelete()
    {
        await using var alice = await ConnectAsync("alice");
        await alice.JoinAsync("#edit");

        var id = await SayAsync(alice, "#edit", "still here");
        var refused = Next<ErrorPayload>(h => alice.ServerError += p => h(p));

        await alice.EditMessageAsync("#edit", id, "");

        // Two verbs, one rule each for who may do what. An edit that quietly removed a message
        // would let the edit rule stand in for the delete rule.
        Assert.Equal("EMPTY_EDIT", (await refused.WaitAsync(Patience)).Code);
        Assert.Equal("still here", (await _store.GetMessageAsync("#edit", id))!.Text);
    }
}
