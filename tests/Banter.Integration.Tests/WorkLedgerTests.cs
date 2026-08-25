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
/// The work ledger (PLAN §8b): posting, claiming, assigning, leases and completion. The rules
/// worth proving are the arbitration (one claim wins) and the lease (crashed work comes back).
/// </summary>
public sealed class WorkLedgerTests : IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private readonly TcpBanterTransport _transport = new();
    private readonly InMemoryAccountStore _accounts = new InMemoryAccountStore()
        .AddUser("human", "pw")
        .AddUser("local", "pw", isAgent: true)
        .AddUser("local-2", "pw", isAgent: true);

    private string _root = null!;
    private BanterDatabase _database = null!;
    private BanterServer _server = null!;

    /// <summary>Short lease and a fast sweep so the reclaim path is testable in seconds.</summary>
    private static readonly TaskLimits Limits = new()
    {
        DefaultLeaseSeconds = 1,
        MaxConcurrentPerAgent = 1,
        SweepInterval = TimeSpan.FromMilliseconds(200),
    };

    public async Task InitializeAsync()
    {
        _root = Path.Combine(Path.GetTempPath(), $"banter-tasks-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _database = new BanterDatabase(BanterStorageOptions.DefaultSqlite(Path.Combine(_root, "banter.db")));
        await _database.InitializeAsync();
        var files = new FileStore(_database, new FileStoreOptions { DataDirectory = Path.Combine(_root, "files") });
        _server = new BanterServer(
            _transport, _accounts, new DbServerStore(_database), files,
            guardrails: null, tasks: new TaskStore(_database), taskLimits: Limits);
        await _server.StartAsync(new Uri("tcp://127.0.0.1:0"));
    }

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        BanterDatabase.ClearSqlitePools();
        Directory.Delete(_root, recursive: true);
    }

    private Task<BanterClient> ConnectAsync(string user) =>
        BanterClient.ConnectAsync(_transport, _server.Endpoint, user, "pw");

    private static async Task<BanterClient> AgentAsync(
        Func<string, Task<BanterClient>> connect, string nick, string room)
    {
        var client = await connect(nick);
        await client.AnnounceAgentAsync(
            new AgentAnnouncePayload(nick, AgentLocality.Local, DataSensitivity.Sensitive, ["chat"], "", 1, false));
        await client.JoinAsync(room);
        return client;
    }

    private static async Task<TaskInfoPayload> WaitForStateAsync(
        BanterClient observer, string room, string taskId, TaskState state)
    {
        var deadline = DateTimeOffset.UtcNow + Timeout;
        TaskState? last = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var list = await observer.ListTasksAsync(room, includeFinished: true);
            var found = list.Tasks.FirstOrDefault(t => t.TaskId == taskId);
            last = found?.State;
            if (found is not null && found.State == state)
            {
                return found;
            }

            await Task.Delay(25);
        }

        Assert.Fail($"Task never reached {state}; last seen {last?.ToString() ?? "(missing)"}.");
        throw new InvalidOperationException();
    }

    [Fact]
    public async Task PostedWorkIsOpenAndVisibleInTheRoom()
    {
        await using var human = await ConnectAsync("human");
        await human.JoinAsync("#main");

        var task = await human.PostTaskAsync("#main", "fix the parser", "it throws on empty input");

        Assert.Equal(TaskState.Open, task.State);
        Assert.Equal("human", task.Poster);
        Assert.Null(task.Assignee);

        var list = await human.ListTasksAsync("#main");
        Assert.Equal(task.TaskId, Assert.Single(list.Tasks).TaskId);
    }

    [Fact]
    public async Task OnlyOneOfTwoSimultaneousClaimsWins()
    {
        await using var human = await ConnectAsync("human");
        await human.JoinAsync("#main");
        await using var a = await AgentAsync(ConnectAsync, "local", "#main");
        await using var b = await AgentAsync(ConnectAsync, "local-2", "#main");

        var task = await human.PostTaskAsync("#main", "race me");

        // Both go for it at once; the engine's single writer is the arbiter.
        var claims = await Task.WhenAll(
            Attempt(() => a.ClaimTaskAsync(task.TaskId)),
            Attempt(() => b.ClaimTaskAsync(task.TaskId)));

        var winners = claims.Where(c => c is not null).ToList();
        Assert.Single(winners);
        Assert.Equal(TaskState.Claimed, winners[0]!.State);

        static async Task<TaskInfoPayload?> Attempt(Func<Task<TaskInfoPayload>> claim)
        {
            try
            {
                return await claim();
            }
            catch (BanterErrorException ex) when (ex.Code == "TASK_TAKEN")
            {
                return null;   // the clean refusal, not duplicate work
            }
        }
    }

    [Fact]
    public async Task AHumanCannotClaimWork()
    {
        await using var human = await ConnectAsync("human");
        await human.JoinAsync("#main");
        var task = await human.PostTaskAsync("#main", "agents only");

        var error = await Assert.ThrowsAsync<BanterErrorException>(() => human.ClaimTaskAsync(task.TaskId));

        Assert.Equal("NOT_AN_AGENT", error.Code);
    }

    [Fact]
    public async Task AnAgentCannotHoldMoreThanTheConcurrencyCap()
    {
        await using var human = await ConnectAsync("human");
        await human.JoinAsync("#main");
        await using var agent = await AgentAsync(ConnectAsync, "local", "#main");

        var first = await human.PostTaskAsync("#main", "one");
        var second = await human.PostTaskAsync("#main", "two");
        await agent.ClaimTaskAsync(first.TaskId);

        // Cap is 1: a greedy agent must not corner the board.
        var error = await Assert.ThrowsAsync<BanterErrorException>(() => agent.ClaimTaskAsync(second.TaskId));

        Assert.Equal("TASK_LIMIT", error.Code);
    }

    [Fact]
    public async Task OnlyTheDelegatorMayAssign()
    {
        await using var human = await ConnectAsync("human");
        await human.JoinAsync("#main");
        await using var a = await AgentAsync(ConnectAsync, "local", "#main");
        await using var b = await AgentAsync(ConnectAsync, "local-2", "#main");

        var task = await human.PostTaskAsync("#main", "assign me");

        // The human posted it but is not the delegator; assignment is the delegator's power.
        var error = await Assert.ThrowsAsync<BanterErrorException>(() => human.AssignTaskAsync(task.TaskId, "local-2"));
        Assert.Equal("NOT_DELEGATOR", error.Code);

        var assigned = await a.AssignTaskAsync(task.TaskId, "local-2");
        Assert.Equal(TaskState.Assigned, assigned.State);
        Assert.Equal("local-2", assigned.Assignee);
    }

    [Fact]
    public async Task AnExpiredLeaseReturnsTheWorkToThePool()
    {
        await using var human = await ConnectAsync("human");
        await human.JoinAsync("#main");
        await using var agent = await AgentAsync(ConnectAsync, "local", "#main");

        var task = await human.PostTaskAsync("#main", "abandon me");
        await agent.ClaimTaskAsync(task.TaskId);

        // The agent goes quiet, which is what a crash looks like from here. The lease is what
        // stops the work disappearing with it.
        var reclaimed = await WaitForStateAsync(human, "#main", task.TaskId, TaskState.Open);

        Assert.Null(reclaimed.Assignee);
        Assert.Null(reclaimed.LeaseExpiresAt);
    }

    [Fact]
    public async Task ProgressRenewsTheLeaseSoLongWorkIsNotReclaimed()
    {
        await using var human = await ConnectAsync("human");
        await human.JoinAsync("#main");
        await using var agent = await AgentAsync(ConnectAsync, "local", "#main");

        var task = await human.PostTaskAsync("#main", "long job");
        await agent.ClaimTaskAsync(task.TaskId);

        // Keep reporting for longer than the 1s lease; it must still be held at the end.
        for (var i = 0; i < 6; i++)
        {
            await Task.Delay(300);
            await agent.UpdateTaskAsync(task.TaskId, $"step {i}");
        }

        var list = await human.ListTasksAsync("#main");
        var held = Assert.Single(list.Tasks);
        Assert.Equal(TaskState.Claimed, held.State);
        Assert.Equal("local", held.Assignee);
    }

    [Fact]
    public async Task OnlyTheHolderMayReportProgressOrFinish()
    {
        await using var human = await ConnectAsync("human");
        await human.JoinAsync("#main");
        await using var a = await AgentAsync(ConnectAsync, "local", "#main");
        await using var b = await AgentAsync(ConnectAsync, "local-2", "#main");

        var task = await human.PostTaskAsync("#main", "mine");
        await b.ClaimTaskAsync(task.TaskId);

        var update = await Assert.ThrowsAsync<BanterErrorException>(() => a.UpdateTaskAsync(task.TaskId, "sneaky"));
        Assert.Equal("NOT_HOLDER", update.Code);

        var done = await Assert.ThrowsAsync<BanterErrorException>(() => a.CompleteTaskAsync(task.TaskId));
        Assert.Equal("NOT_HOLDER", done.Code);
    }

    [Fact]
    public async Task CompletionAndFailureAreBothTerminalAndRecorded()
    {
        await using var human = await ConnectAsync("human");
        await human.JoinAsync("#main");
        await using var agent = await AgentAsync(ConnectAsync, "local", "#main");

        var ok = await human.PostTaskAsync("#main", "will succeed");
        await agent.ClaimTaskAsync(ok.TaskId);
        await agent.CompleteTaskAsync(ok.TaskId, "all green");
        var succeeded = await WaitForStateAsync(human, "#main", ok.TaskId, TaskState.Done);
        Assert.Equal("all green", succeeded.Result);

        var bad = await human.PostTaskAsync("#main", "will fail");
        await agent.ClaimTaskAsync(bad.TaskId);
        await agent.CompleteTaskAsync(bad.TaskId, "endpoint down", success: false);
        var failed = await WaitForStateAsync(human, "#main", bad.TaskId, TaskState.Failed);
        Assert.Equal("endpoint down", failed.Result);

        // Terminal tasks drop off the live board but stay in the record.
        var live = await human.ListTasksAsync("#main");
        Assert.Empty(live.Tasks);
        var all = await human.ListTasksAsync("#main", includeFinished: true);
        Assert.Equal(2, all.Tasks.Count);
    }

    [Fact]
    public async Task EveryStateChangeIsAnnouncedInTheRoom()
    {
        await using var human = await ConnectAsync("human");
        await human.JoinAsync("#main");
        await using var agent = await AgentAsync(ConnectAsync, "local", "#main");

        var task = await human.PostTaskAsync("#main", "trace me");
        await agent.ClaimTaskAsync(task.TaskId);
        await agent.CompleteTaskAsync(task.TaskId, "finished");

        // The timeline is the audit trail (§8b), so each transition has to be in it.
        var deadline = DateTimeOffset.UtcNow + Timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var history = await human.GetHistoryAsync("#main", limit: 200);
            var text = string.Join("\n", history.Messages.Select(m => m.Text));
            if (text.Contains("posted by human") && text.Contains("claimed by local") && text.Contains("done by local"))
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.Fail("Task transitions were not all announced in the room.");
    }

    [Fact]
    public async Task ReleasingPutsWorkBackForSomeoneElse()
    {
        await using var human = await ConnectAsync("human");
        await human.JoinAsync("#main");
        await using var a = await AgentAsync(ConnectAsync, "local", "#main");
        await using var b = await AgentAsync(ConnectAsync, "local-2", "#main");

        var task = await human.PostTaskAsync("#main", "hand me over");
        await b.ClaimTaskAsync(task.TaskId);
        await b.ReleaseTaskAsync(task.TaskId, "out of my depth");

        var claimed = await a.ClaimTaskAsync(task.TaskId);
        Assert.Equal("local", claimed.Assignee);
    }
}
