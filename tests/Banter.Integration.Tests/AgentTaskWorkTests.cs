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
/// Agents working the ledger (PLAN §8b end to end): claiming, executing, reporting, and what
/// happens when a job outlives its lease or the agent stops mid-task.
/// </summary>
public sealed class AgentTaskWorkTests : IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);
    private readonly TcpBanterTransport _transport = new();
    private readonly InMemoryAccountStore _accounts = new InMemoryAccountStore()
        .AddUser("human", "pw")
        .AddUser("coder", "pw", isAgent: true)
        .AddUser("writer", "pw", isAgent: true);

    private string _root = null!;
    private BanterDatabase _database = null!;
    private BanterServer _server = null!;

    /// <summary>Two-second lease so the renew path is observable without a slow test.</summary>
    private static readonly TaskLimits Limits = new()
    {
        DefaultLeaseSeconds = 2,
        MaxConcurrentPerAgent = 1,
        SweepInterval = TimeSpan.FromMilliseconds(200),
    };

    /// <summary>An agent whose answer is fixed, optionally after a delay.</summary>
    private sealed class WorkerAgent(BanterAgentOptions options, string answer, TimeSpan? delay = null)
        : BanterAgent(options)
    {
        public List<string> Prompts { get; } = [];

        protected override async IAsyncEnumerable<string> RespondAsync(
            string room, string sender, string prompt,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Prompts.Add(prompt);
            if (delay is { } d)
            {
                await Task.Delay(d, cancellationToken);
            }
            else
            {
                await Task.Yield();
            }

            yield return answer;
        }
    }

    public async Task InitializeAsync()
    {
        _root = Path.Combine(Path.GetTempPath(), $"banter-taskwork-{Guid.NewGuid():N}");
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

    private BanterAgentOptions Worker(string nick, string[] skills, bool claims = true) => new()
    {
        Server = _server.Endpoint,
        User = nick,
        Password = "pw",
        Rooms = ["#main"],
        Locality = AgentLocality.Local,
        Clearance = DataSensitivity.Sensitive,
        Skills = skills,
        TaskWork = new TaskWorkOptions
        {
            ClaimOpenTasks = claims,
            // Well inside the 2s lease, so a long job keeps renewing.
            ProgressInterval = TimeSpan.FromMilliseconds(600),
        },
    };

    private static async Task WaitForDelegatorAsync(BanterClient observer, string expected)
    {
        var deadline = DateTimeOffset.UtcNow + Timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var agents = await observer.GetAgentsAsync("#main");
            if (agents.Agents.Any(a => a.IsDelegator && a.Nick == expected))
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.Fail($"'{expected}' never became delegator.");
    }

    private static async Task<TaskInfoPayload> WaitForStateAsync(
        BanterClient observer, string taskId, TaskState state, TimeSpan? within = null)
    {
        var deadline = DateTimeOffset.UtcNow + (within ?? Timeout);
        TaskState? last = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var list = await observer.ListTasksAsync("#main", includeFinished: true);
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
    public async Task AnAgentClaimsMatchingWorkDoesItAndReportsTheResult()
    {
        await using var human = await BanterClient.ConnectAsync(_transport, _server.Endpoint, "human", "pw");
        await human.JoinAsync("#main");

        await using var coder = new WorkerAgent(Worker("coder", ["code"]), "patched the parser");
        await coder.StartAsync(_transport);

        var task = await human.PostTaskAsync("#main", "fix the code in the parser");

        var done = await WaitForStateAsync(human, task.TaskId, TaskState.Done);
        Assert.Equal("coder", done.Assignee);
        Assert.Equal("patched the parser", done.Result);
        Assert.Contains("fix the code", Assert.Single(coder.Prompts));
    }

    [Fact]
    public async Task WorkThatDoesNotMatchAnAgentsSkillsIsLeftOnTheBoard()
    {
        await using var human = await BanterClient.ConnectAsync(_transport, _server.Endpoint, "human", "pw");
        await human.JoinAsync("#main");

        await using var coder = new WorkerAgent(Worker("coder", ["code"]), "should not run");
        await coder.StartAsync(_transport);

        var task = await human.PostTaskAsync("#main", "write the launch announcement");

        // Give it long enough that a wrong claim would have happened.
        await Task.Delay(1500);

        var list = await human.ListTasksAsync("#main");
        Assert.Equal(TaskState.Open, list.Tasks.Single(t => t.TaskId == task.TaskId).State);
        Assert.Empty(coder.Prompts);
    }

    [Fact]
    public async Task TheRightSpecialistPicksUpTheWork()
    {
        await using var human = await BanterClient.ConnectAsync(_transport, _server.Endpoint, "human", "pw");
        await human.JoinAsync("#main");

        await using var coder = new WorkerAgent(Worker("coder", ["code"]), "code done");
        await coder.StartAsync(_transport);
        await using var writer = new WorkerAgent(Worker("writer", ["docs"]), "docs done");
        await writer.StartAsync(_transport);

        var task = await human.PostTaskAsync("#main", "draft the docs for the new endpoint");

        var done = await WaitForStateAsync(human, task.TaskId, TaskState.Done);
        Assert.Equal("writer", done.Assignee);
        Assert.Equal("docs done", done.Result);
    }

    [Fact]
    public async Task AJobLongerThanTheLeaseKeepsItByReportingProgress()
    {
        await using var human = await BanterClient.ConnectAsync(_transport, _server.Endpoint, "human", "pw");
        await human.JoinAsync("#main");

        // 5s of work against a 2s lease: without the renew heartbeat the server would reclaim it
        // mid-flight and a second agent could start the same job.
        await using var coder = new WorkerAgent(
            Worker("coder", ["code"]), "slow but finished", delay: TimeSpan.FromSeconds(5));
        await coder.StartAsync(_transport);

        var task = await human.PostTaskAsync("#main", "a slow code job");

        var done = await WaitForStateAsync(human, task.TaskId, TaskState.Done, TimeSpan.FromSeconds(25));
        Assert.Equal("coder", done.Assignee);
        Assert.Equal("slow but finished", done.Result);
    }

    [Fact]
    public async Task AnAgentThatOnlyTakesAssignedWorkIgnoresTheOpenBoard()
    {
        await using var human = await BanterClient.ConnectAsync(_transport, _server.Endpoint, "human", "pw");
        await human.JoinAsync("#main");

        // Delegated rooms want routing to be somebody's job, not a free-for-all.
        // 'writer' starts first so it wins the election and can assign.
        await using var delegator = new WorkerAgent(Worker("writer", ["docs"], claims: false), "delegator");
        await delegator.StartAsync(_transport);
        await WaitForDelegatorAsync(human, "writer");

        await using var coder = new WorkerAgent(Worker("coder", ["code"], claims: false), "did it");
        await coder.StartAsync(_transport);

        var task = await human.PostTaskAsync("#main", "some code work");
        await Task.Delay(1200);
        Assert.Equal(TaskState.Open, (await human.ListTasksAsync("#main")).Tasks.Single().State);

        // But an assignment still runs: that is the delegator handing work over.
        await delegator.AssignTaskAsync(task.TaskId, "coder");

        var done = await WaitForStateAsync(human, task.TaskId, TaskState.Done);
        Assert.Equal("coder", done.Assignee);
        Assert.Equal("did it", done.Result);
    }

    [Fact]
    public async Task AFailingAgentRecordsAFailureRatherThanHoldingTheTask()
    {
        await using var human = await BanterClient.ConnectAsync(_transport, _server.Endpoint, "human", "pw");
        await human.JoinAsync("#main");

        await using var broken = new WorkerAgent(Worker("coder", ["code"]), answer: "");
        await broken.StartAsync(_transport);

        var task = await human.PostTaskAsync("#main", "some code work");

        // An agent that produces nothing has not done the work, and saying so is more useful
        // than silently marking it done.
        var failed = await WaitForStateAsync(human, task.TaskId, TaskState.Failed);
        Assert.Equal("produced no output", failed.Result);
    }

    [Fact]
    public async Task TaskTransitionsAreVisibleToTheAgentThatRanThem()
    {
        await using var human = await BanterClient.ConnectAsync(_transport, _server.Endpoint, "human", "pw");
        await human.JoinAsync("#main");

        await using var coder = new WorkerAgent(Worker("coder", ["code"]), "done");
        var started = new List<string>();
        var finished = new List<bool>();
        coder.TaskStarted += t => started.Add(t.TaskId);
        coder.TaskFinished += (_, ok) => finished.Add(ok);
        await coder.StartAsync(_transport);

        var task = await human.PostTaskAsync("#main", "code something");
        await WaitForStateAsync(human, task.TaskId, TaskState.Done);

        Assert.Equal(task.TaskId, Assert.Single(started));
        Assert.True(Assert.Single(finished));
    }
}
