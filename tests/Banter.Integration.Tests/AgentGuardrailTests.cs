using Banter.Client.Core;
using Banter.Core;
using Banter.Protocol;
using Banter.Protocol.Transport;
using Banter.Server;
using Banter.Server.Files;
using Banter.Server.Persistence;
using Xunit;

namespace Banter.Integration.Tests;

/// <summary>Agent chatter limits (PLAN §5): the rate limit and the loop-breaker that stop two
/// agents talking to each other forever.</summary>
public sealed class AgentGuardrailTests : IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private readonly TcpBanterTransport _transport = new();
    private readonly InMemoryAccountStore _accounts = new InMemoryAccountStore()
        .AddUser("human", "pw-h")
        .AddUser("agent-a", "pw-a", isAgent: true)
        .AddUser("agent-b", "pw-b", isAgent: true);
    private string _root = null!;
    private BanterDatabase _database = null!;
    private BanterServer _server = null!;

    // Small limits keep the tests fast and the intent obvious.
    private static readonly AgentGuardrails Limits = new()
    {
        MaxAgentMessagesPerMinute = 6,
        MaxConsecutiveAgentMessages = 3,
    };

    public async Task InitializeAsync()
    {
        _root = Path.Combine(Path.GetTempPath(), $"banter-guard-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _database = new BanterDatabase(BanterStorageOptions.DefaultSqlite(Path.Combine(_root, "banter.db")));
        await _database.InitializeAsync();
        var files = new FileStore(_database, new FileStoreOptions { DataDirectory = Path.Combine(_root, "files") });
        _server = new BanterServer(_transport, _accounts, new DbServerStore(_database), files, Limits);
        await _server.StartAsync(new Uri("tcp://127.0.0.1:0"));
    }

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        BanterDatabase.ClearSqlitePools();
        Directory.Delete(_root, recursive: true);
    }

    private Task<BanterClient> ConnectAsync(string user, string secret) =>
        BanterClient.ConnectAsync(_transport, _server.Endpoint, user, secret);

    /// <summary>Sends and waits for the message to land in history, so the single-writer engine
    /// has definitely processed it before the next assertion.</summary>
    private static async Task SendAndSettleAsync(BanterClient client, string room, string text)
    {
        await client.SendMessageAsync(room, text);
        var deadline = DateTimeOffset.UtcNow + Timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var history = await client.GetHistoryAsync(room, limit: 200);
            if (history.Messages.Any(m => m.Text == text))
            {
                return;
            }

            await Task.Delay(20);
        }

        Assert.Fail($"'{text}' never reached {room}.");
    }

    /// <summary>Sends and waits for the server's refusal, which arrives as an unmatched error
    /// on the <see cref="BanterClient.ServerError"/> channel.</summary>
    private static async Task<ErrorPayload> SendAndExpectRefusalAsync(BanterClient client, string room, string text)
    {
        var refused = new TaskCompletionSource<ErrorPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(ErrorPayload error) => refused.TrySetResult(error);
        client.ServerError += Handler;
        try
        {
            await client.SendMessageAsync(room, text);
            return await refused.Task.WaitAsync(Timeout);
        }
        finally
        {
            client.ServerError -= Handler;
        }
    }

    [Fact]
    public async Task HumansAreNeverThrottled()
    {
        await using var human = await ConnectAsync("human", "pw-h");
        await human.JoinAsync("#free");

        // Well past the agent limits; every one must land.
        var count = Limits.MaxAgentMessagesPerMinute * 2;
        for (var i = 0; i < count; i++)
        {
            await SendAndSettleAsync(human, "#free", $"human {i}");
        }

        var history = await human.GetHistoryAsync("#free", limit: 200);
        Assert.Equal(count, history.Messages.Count(m => m.Sender == "human"));
    }

    [Fact]
    public async Task LoopBreakerStopsAgentsTalkingToEachOtherAndAHumanClearsIt()
    {
        await using var human = await ConnectAsync("human", "pw-h");
        await using var agentA = await ConnectAsync("agent-a", "pw-a");
        await using var agentB = await ConnectAsync("agent-b", "pw-b");
        await human.JoinAsync("#loop");
        await agentA.JoinAsync("#loop");
        await agentB.JoinAsync("#loop");

        // Agents ping-pong until the breaker trips (MaxConsecutiveAgentMessages = 3).
        await SendAndSettleAsync(agentA, "#loop", "a1");
        await SendAndSettleAsync(agentB, "#loop", "b1");
        await SendAndSettleAsync(agentA, "#loop", "a2");

        var refusal = await SendAndExpectRefusalAsync(agentB, "#loop", "b2");
        Assert.Equal("LOOP_BROKEN", refusal.Code);

        // The room was told why it went quiet.
        var history = await human.GetHistoryAsync("#loop", limit: 200);
        Assert.Contains(history.Messages, m =>
            m.Sender == AgentGuardrails.SystemNick && m.Text.Contains("Loop-breaker tripped"));
        Assert.DoesNotContain(history.Messages, m => m.Text == "b2");

        // A human speaking clears it, and agents can talk again.
        await SendAndSettleAsync(human, "#loop", "carry on");
        await SendAndSettleAsync(agentA, "#loop", "resumed");

        history = await human.GetHistoryAsync("#loop", limit: 200);
        Assert.Contains(history.Messages, m =>
            m.Sender == AgentGuardrails.SystemNick && m.Text.Contains("Loop-breaker cleared"));
    }

    [Fact]
    public async Task RateLimitRefusesAgentBurstsWithinTheMinute()
    {
        await using var human = await ConnectAsync("human", "pw-h");
        await using var agent = await ConnectAsync("agent-a", "pw-a");
        await human.JoinAsync("#burst");
        await agent.JoinAsync("#burst");

        // A human message between each agent message resets the consecutive counter, so only
        // the sliding-minute window is under test here.
        for (var i = 0; i < Limits.MaxAgentMessagesPerMinute; i++)
        {
            await SendAndSettleAsync(agent, "#burst", $"agent {i}");
            await SendAndSettleAsync(human, "#burst", $"human {i}");
        }

        var refusal = await SendAndExpectRefusalAsync(agent, "#burst", "one too many");
        Assert.Equal("THROTTLED", refusal.Code);
    }

    [Fact]
    public async Task StreamStartIsChargedAgainstTheLimits()
    {
        await using var agent = await ConnectAsync("agent-a", "pw-a");
        await agent.JoinAsync("#streamloop");

        for (var i = 0; i < Limits.MaxConsecutiveAgentMessages; i++)
        {
            await using var stream = await agent.StartMessageStreamAsync("#streamloop");
            await stream.CompleteAsync($"stream {i}");
        }

        // START is a request, so the refusal comes back as an exception rather than an event.
        var ex = await Assert.ThrowsAsync<BanterErrorException>(() => agent.StartMessageStreamAsync("#streamloop"));
        Assert.Equal("LOOP_BROKEN", ex.Code);
    }
}
