using Banter.Agents.Sdk;
using Banter.Client.Core;
using Banter.Core;
using Banter.Protocol;
using Banter.Protocol.Transport;
using Banter.Server;
using Banter.Server.Files;
using Banter.Server.Persistence;
using Banter.Warden;
using Xunit;
using Xunit.Abstractions;

namespace Banter.Integration.Tests;

/// <summary>
/// The agent behind the ask demo (PLAN §8c-a).
///
/// <para>It exists so the controls can be looked at and answered without a model in the way, and
/// it is tested for the same reason the demo exists: a broken demo is discovered by someone
/// sitting down to try the feature, at the worst possible moment, and it looks like the feature
/// is broken rather than the demo.</para>
/// </summary>
public sealed class DemoAskAgentTests(ITestOutputHelper output) : IAsyncLifetime
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private readonly TcpBanterTransport _transport = new();
    private readonly InMemoryAccountStore _accounts = new InMemoryAccountStore()
        .AddUser("nell", "pw")
        .AddUser("asker", "pw", isAgent: true);

    private string _root = null!;
    private BanterDatabase _database = null!;
    private BanterServer _server = null!;

    public async Task InitializeAsync()
    {
        _root = Path.Combine(Path.GetTempPath(), $"banter-demo-{Guid.NewGuid():N}");
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

    private DemoAskAgent Agent() => new(new BanterAgentOptions
    {
        Server = _server.Endpoint,
        User = "asker",
        Password = "pw",
        Rooms = ["#main"],
        Locality = AgentLocality.Local,
        Clearance = DataSensitivity.Sensitive,
        Skills = ["chat"],
        AskTimeout = TimeSpan.FromSeconds(5),
    });

    private async Task<BanterClient> JoinAsync(string nick)
    {
        var client = await BanterClient.ConnectAsync(_transport, _server.Endpoint, nick, "pw");
        await client.JoinAsync("#main");
        return client;
    }

    private static async Task<AskPayload> AskedAsync(BanterClient watcher, Func<Task> act)
    {
        var seen = new TaskCompletionSource<AskPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(AskPayload a) => seen.TrySetResult(a);
        watcher.AskReceived += Handler;
        try
        {
            await act();
            return await seen.Task.WaitAsync(Patience);
        }
        finally
        {
            watcher.AskReceived -= Handler;
        }
    }

    /// <summary>
    /// Each scenario, by the word that triggers it and the shape it is there to show. If one of
    /// these stops producing what it claims, the demo is quietly showing the wrong thing.
    /// </summary>
    public static TheoryData<string, int, bool, bool> Scenarios() => new()
    {
        // word, questions, multi-select, has options
        { "confirm", 1, false, true },
        { "pick", 1, false, true },
        { "many", 1, true, true },
        { "tabs", 3, false, true },
        { "write", 1, false, false },
    };

    [Theory]
    [MemberData(nameof(Scenarios))]
    public async Task EachScenarioAsksTheShapeItAdvertises(
        string word, int questions, bool multiSelect, bool hasOptions)
    {
        await using var nell = await JoinAsync("nell");
        await using var asker = Agent();
        await asker.StartAsync(_transport);

        var ask = await AskedAsync(nell, () => nell.SendMessageAsync("#main", $"@asker {word}").AsTask());
        output.WriteLine($"{word}: {ask.Questions.Count} question(s) — "
            + string.Join(" | ", ask.Questions.Select(q => $"{q.Header}[{q.Options.Count}]")));

        Assert.Equal(questions, ask.Questions.Count);
        Assert.Equal(multiSelect, ask.Questions[0].MultiSelect);
        Assert.Equal(hasOptions, ask.Questions[0].Options.Count > 0);

        // Every question needs a key to be answerable, and a header to be a tab.
        Assert.All(ask.Questions, q =>
        {
            Assert.NotEqual("", q.Key);
            Assert.NotEqual("", q.Header);
            Assert.NotEqual("", q.Text);
        });
    }

    [Fact]
    public async Task TheAnswerComesBackToTheRoomSoTheRoundTripIsVisible()
    {
        await using var nell = await JoinAsync("nell");
        await using var asker = Agent();
        await asker.StartAsync(_transport);

        var ask = await AskedAsync(nell, () => nell.SendMessageAsync("#main", "@asker confirm").AsTask());
        await nell.AnswerAsync(ask.AskId, [new AskAnswer("deploy", ["no"], "not until the review lands")]);

        // Said back in the room, because somebody testing the free-text box has no other way to
        // see that what they typed reached the agent rather than just closing the panel.
        var said = await UntilAsync(nell, m => m.Sender == "asker" && m.Text.StartsWith("[confirm]", StringComparison.Ordinal));
        output.WriteLine(said);

        Assert.Contains("nell answered", said, StringComparison.Ordinal);
        Assert.Contains("not until the review lands", said, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SayingNothingItUnderstandsOffersTheMenuRatherThanAsking()
    {
        await using var nell = await JoinAsync("nell");
        await using var asker = Agent();
        await asker.StartAsync(_transport);

        await nell.SendMessageAsync("#main", "@asker hello");
        var said = await UntilAsync(nell, m => m.Sender == "asker");
        output.WriteLine(said);

        // Somebody who has just started the demo has no idea what to type. Guessing wrong should
        // tell them, not open a question they did not want.
        Assert.All(new[] { "confirm", "pick", "many", "tabs", "write" },
            word => Assert.Contains(word, said, StringComparison.Ordinal));
    }

    private static async Task<string> UntilAsync(BanterClient client, Func<MsgPayload, bool> predicate)
    {
        var deadline = DateTimeOffset.UtcNow + Patience;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var history = await client.GetHistoryAsync("#main", limit: 50);
            if (history.Messages.FirstOrDefault(predicate) is { } hit)
            {
                return hit.Text;
            }

            await Task.Delay(25);
        }

        throw new Xunit.Sdk.XunitException("no such message arrived");
    }
}
