using System.Runtime.CompilerServices;
using Banter.Agents.Sdk;
using Banter.Client.Core;
using Banter.Core;
using Banter.Protocol;
using Banter.Protocol.Transport;
using Banter.Server;
using Banter.Server.Files;
using Banter.Server.Persistence;
using Xunit;

namespace Banter.Integration.Tests;

/// <summary>
/// Putting people and agents into a room and taking them out - by an admin, or by a delegator for
/// the agents in the room it runs - and a room somebody was put into surviving a reconnect.
/// </summary>
public sealed class RoomMembershipTests : IAsyncLifetime
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(15);

    private readonly TcpBanterTransport _transport = new();
    private readonly InMemoryAccountStore _accounts = new InMemoryAccountStore()
        .AddUser("admin", "pw", isAgent: false, isAdmin: true)
        .AddUser("member", "pw")
        .AddUser("local", "pw", isAgent: true)
        .AddUser("local-2", "pw", isAgent: true);

    private string _root = null!;
    private BanterDatabase _database = null!;
    private FileStore _files = null!;
    private BanterServer _server = null!;

    private sealed class QuietAgent(BanterAgentOptions options) : BanterAgent(options)
    {
        public Task RemoveForTestAsync(string nick, string room) => Client.RemoveFromRoomAsync(nick, room);

        protected override async IAsyncEnumerable<string> RespondAsync(
            string room, string sender, string prompt,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            yield return "ok";
        }
    }

    public async Task InitializeAsync()
    {
        _root = Path.Combine(Path.GetTempPath(), $"banter-members-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _database = new BanterDatabase(BanterStorageOptions.DefaultSqlite(Path.Combine(_root, "banter.db")));
        await _database.InitializeAsync();
        _files = new FileStore(_database, new FileStoreOptions { DataDirectory = Path.Combine(_root, "files") });
        _server = new BanterServer(_transport, _accounts, new DbServerStore(_database), _files);
        await _server.StartAsync(new Uri("tcp://127.0.0.1:0"));
    }

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        BanterDatabase.ClearSqlitePools();
        Directory.Delete(_root, recursive: true);
    }

    private BanterAgentOptions Agent(string nick) => new()
    {
        Server = _server.Endpoint,
        User = nick,
        Password = "pw",
        Rooms = ["#main"],
        Locality = AgentLocality.Local,
        Clearance = DataSensitivity.Sensitive,
        Skills = ["chat"],
    };

    private async Task<BanterClient> PersonAsync(string nick, params string[] rooms)
    {
        var client = await BanterClient.ConnectAsync(_transport, _server.Endpoint, nick, "pw");
        foreach (var room in rooms)
        {
            await client.JoinAsync(room);
        }

        return client;
    }

    private static async Task UntilAsync(Func<Task<bool>> condition, string what)
    {
        var deadline = DateTimeOffset.UtcNow + Patience;
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                if (await condition())
                {
                    return;
                }
            }
            catch (BanterClientException)
            {
                // Mid-reconnect; ask again.
            }

            await Task.Delay(50);
        }

        Assert.Fail(what);
    }

    private static async Task<bool> InAsync(BanterClient watcher, string room, string nick) =>
        (await watcher.GetMembersAsync(room)).Members.Any(m => m.Nick == nick);

    private static async Task<string?> DelegatorAsync(BanterClient watcher, string room) =>
        (await watcher.GetAgentsAsync(room)).Agents.FirstOrDefault(a => a.IsDelegator)?.Nick;

    /// <summary>
    /// The case this was written for: the agent running a room is taken out of it, and an admin
    /// puts it back - in, on the roster, and running the room again.
    /// </summary>
    [Fact]
    public async Task AnAdminPutsBackAnAgentThatWasTakenOut()
    {
        await using var admin = await PersonAsync("admin", "#main");
        await using var local = new QuietAgent(Agent("local"));
        await local.StartAsync(_transport);
        await UntilAsync(async () => await DelegatorAsync(admin, "#main") == "local", "local never ran #main");

        await admin.RemoveFromRoomAsync("local", "#main", "testing");
        await UntilAsync(async () => !await InAsync(admin, "#main", "local"), "local was not taken out");
        Assert.Null(await DelegatorAsync(admin, "#main"));

        await admin.AddToRoomAsync("local", "#main");
        await UntilAsync(async () => await InAsync(admin, "#main", "local"), "local was not put back");
        await UntilAsync(async () => await DelegatorAsync(admin, "#main") == "local", "local was not re-elected");
    }

    [Fact]
    public async Task AnAdminCanAddAPersonToARoomTheyAreNotIn()
    {
        await using var admin = await PersonAsync("admin");
        await using var member = await PersonAsync("member", "#main");
        await admin.CreateRoomAsync("#side");

        var joined = new TaskCompletionSource<JoinPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
        member.MemberJoined += j =>
        {
            if (j.Nick == "member" && j.Room == "#side")
            {
                joined.TrySetResult(j);
            }
        };

        await admin.AddToRoomAsync("member", "#side");

        await joined.Task.WaitAsync(Patience);
        Assert.True(await InAsync(admin, "#side", "member"));
    }

    [Fact]
    public async Task APersonCannotAddOrRemove()
    {
        await using var admin = await PersonAsync("admin", "#main");
        await using var member = await PersonAsync("member", "#main");
        await using var local = new QuietAgent(Agent("local"));
        await local.StartAsync(_transport);
        await UntilAsync(async () => await InAsync(admin, "#main", "local"), "local never joined");

        var remove = await Assert.ThrowsAsync<BanterErrorException>(() => member.RemoveFromRoomAsync("local", "#main"));
        Assert.Equal("NOT_ALLOWED", remove.Code);

        await admin.CreateRoomAsync("#side");
        var add = await Assert.ThrowsAsync<BanterErrorException>(() => member.AddToRoomAsync("admin", "#side"));
        Assert.Equal("NOT_IN_ROOM", add.Code);
    }

    [Fact]
    public async Task ADelegatorMayRemoveAnAgentButNotAPerson()
    {
        await using var admin = await PersonAsync("admin", "#main");
        await using var local = new QuietAgent(Agent("local"));
        await local.StartAsync(_transport);
        await UntilAsync(async () => await DelegatorAsync(admin, "#main") == "local", "local never ran #main");
        await using var helper = new QuietAgent(Agent("local-2"));
        await helper.StartAsync(_transport);
        await UntilAsync(async () => await InAsync(admin, "#main", "local-2"), "local-2 never joined");

        await local.RemoveForTestAsync("local-2", "#main");
        await UntilAsync(async () => !await InAsync(admin, "#main", "local-2"), "the delegator could not remove an agent");

        var refused = await Assert.ThrowsAsync<BanterErrorException>(() => local.RemoveForTestAsync("admin", "#main"));
        Assert.Equal("NOT_ALLOWED", refused.Code);
    }

    /// <summary>
    /// A room somebody was put into is theirs to rejoin after a reconnect, and one they were taken
    /// out of is not. Only rooms a client had joined itself used to come back.
    /// </summary>
    [Fact]
    public async Task ARoomYouWerePutInSurvivesARestartAndOneYouWereTakenOutOfDoesNot()
    {
        var port = _server.Endpoint.Port;
        await using var admin = await PersonAsync("admin", "#main");
        await using var member = await PersonAsync("member", "#main");
        await admin.CreateRoomAsync("#put-in");
        await admin.CreateRoomAsync("#taken-out");
        await admin.AddToRoomAsync("member", "#put-in");
        await admin.AddToRoomAsync("member", "#taken-out");
        await UntilAsync(async () => await InAsync(admin, "#taken-out", "member"), "member never added");
        await admin.RemoveFromRoomAsync("member", "#taken-out");
        await UntilAsync(async () => !await InAsync(admin, "#taken-out", "member"), "member never removed");

        var back = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        member.Reconnected += () => back.TrySetResult();
        await _server.DisposeAsync();
        _server = new BanterServer(_transport, _accounts, new DbServerStore(_database), _files);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await _server.StartAsync(new Uri($"tcp://127.0.0.1:{port}"));
                break;
            }
            catch (System.Net.Sockets.SocketException) when (attempt < 20)
            {
                await Task.Delay(100);
            }
        }

        await back.Task.WaitAsync(Patience);

        await UntilAsync(async () => await InAsync(admin, "#put-in", "member"), "member was not rejoined to a room it was put in");
        await Task.Delay(500);
        Assert.False(await InAsync(admin, "#taken-out", "member"), "member was put back in a room it was taken out of");
    }
}
