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
using Xunit.Abstractions;

namespace Banter.Integration.Tests;

/// <summary>
/// An agent asking a question with the ways it can be answered.
///
/// <para>The shape matters as much as the feature. A modal would stop the whole client to ask one
/// agent's question while other agents are working in the room and other people are reading it, so
/// the choices hang off a message: the room carries on, anyone can answer, and the question is
/// still there an hour later.</para>
/// </summary>
public sealed class AskTests(ITestOutputHelper output) : IAsyncLifetime
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private readonly TcpBanterTransport _transport = new();
    private readonly InMemoryAccountStore _accounts = new InMemoryAccountStore()
        .AddUser("human", "pw")
        .AddUser("other", "pw")
        .AddUser("scribe", "pw", isAgent: true);

    private string _root = null!;
    private BanterDatabase _database = null!;
    private BanterServer _server = null!;

    private sealed class AskingAgent(BanterAgentOptions options) : BanterAgent(options)
    {
        public string? Answer { get; private set; }
        public string Args { get; set; } = "";

        protected override async IAsyncEnumerable<string> RespondAsync(
            string room, string sender, string prompt,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var result = await InvokeToolAsync("ask_operator", Args, room, cancellationToken)
                .ConfigureAwait(false);
            Answer = result.Content;
            yield return "done";
        }
    }

    public async Task InitializeAsync()
    {
        _root = Path.Combine(Path.GetTempPath(), $"banter-ask-{Guid.NewGuid():N}");
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

    private BanterAgentOptions AgentOptions => new()
    {
        Server = _server.Endpoint,
        User = "scribe",
        Password = "pw",
        Rooms = ["#main"],
        Locality = AgentLocality.Local,
        Clearance = DataSensitivity.Sensitive,
        Skills = ["chat"],
        AskTimeout = TimeSpan.FromSeconds(6),
    };

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

    private const string OneChoice = """
        {"questions":[{"key":"extra","header":"Extra agent","question":"This needs web search. May I bring one in?",
          "options":[{"value":"yes","label":"Yes","description":"Add an agent that can search."},
                     {"value":"no","label":"No","description":"Carry on without."}]}]}
        """;

    [Fact]
    public async Task TheRoomSeesTheQuestionAndItsOptions()
    {
        await using var human = await JoinAsync("human");
        await using var scribe = new AskingAgent(AgentOptions) { Args = OneChoice };
        await scribe.StartAsync(_transport);

        var ask = await AskedAsync(human, () => human.SendMessageAsync("#main", "@scribe start").AsTask());

        output.WriteLine($"{ask.Asker} asked {ask.Questions.Count}: {ask.Questions[0].Text}");
        Assert.Equal("scribe", ask.Asker);
        var question = Assert.Single(ask.Questions);
        Assert.Equal("Extra agent", question.Header);
        Assert.Equal(["Yes", "No"], question.Options.Select(o => o.Label));

        // Free text is on by default: the useful reply to "which of these two" is often "neither,
        // do this instead", and buttons that cannot say so make people pick the nearest wrong one.
        Assert.True(question.AllowText);

        // And it is a message too, so a client that knows nothing about asks still shows it and
        // the history reads correctly afterwards.
        var said = await human.GetHistoryAsync("#main", limit: 50);
        Assert.Contains(said.Messages, m => m.Sender == "scribe" && m.Text.StartsWith("[asking]", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnAnswerReachesTheAgentThatAsked()
    {
        await using var human = await JoinAsync("human");
        await using var scribe = new AskingAgent(AgentOptions) { Args = OneChoice };
        await scribe.StartAsync(_transport);

        var ask = await AskedAsync(human, () => human.SendMessageAsync("#main", "@scribe start").AsTask());
        await human.AnswerAsync(ask.AskId, [new AskAnswer("extra", ["yes"], "")]);

        await UntilAsync(() => scribe.Answer is not null, "the agent never got its answer");
        output.WriteLine(scribe.Answer!);

        // Labels, not raw values: what came back should read the way the person read it.
        Assert.Contains("human answered", scribe.Answer!, StringComparison.Ordinal);
        Assert.Contains("Yes", scribe.Answer!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhatSomebodyTypesOutranksWhatTheyClicked()
    {
        await using var human = await JoinAsync("human");
        await using var scribe = new AskingAgent(AgentOptions) { Args = OneChoice };
        await scribe.StartAsync(_transport);

        var ask = await AskedAsync(human, () => human.SendMessageAsync("#main", "@scribe start").AsTask());
        await human.AnswerAsync(ask.AskId, [new AskAnswer("extra", ["no"], "not yet — ask me again after the review")]);

        await UntilAsync(() => scribe.Answer is not null, "the agent never got its answer");
        output.WriteLine(scribe.Answer!);

        // Both are carried. Somebody who writes something wrote it because the buttons did not
        // say what they meant, so the words must not be dropped in favour of the click.
        Assert.Contains("ask me again after the review", scribe.Answer!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnybodyInTheRoomMayAnswerAndTheFirstAnswerSettlesIt()
    {
        await using var human = await JoinAsync("human");
        await using var other = await JoinAsync("other");
        await using var scribe = new AskingAgent(AgentOptions) { Args = OneChoice };
        await scribe.StartAsync(_transport);

        var closed = new TaskCompletionSource<AskClosedPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
        human.AskClosed += c => closed.TrySetResult(c);

        var ask = await AskedAsync(human, () => human.SendMessageAsync("#main", "@scribe start").AsTask());

        // Somebody other than the person who prompted it answers — a question belongs to the room.
        await other.AnswerAsync(ask.AskId, [new AskAnswer("extra", ["yes"], "")]);

        // Everyone else is told to stop offering it: two people answering the same question is not
        // a conflict worth resolving, it is one that should not arise.
        var settled = await closed.Task.WaitAsync(Patience);
        Assert.Equal("other", settled.AnsweredBy);

        var second = await Assert.ThrowsAsync<BanterErrorException>(
            () => human.AnswerAsync(ask.AskId, [new AskAnswer("extra", ["no"], "")]));
        Assert.Equal("ASK_CLOSED", second.Code);
    }

    [Fact]
    public async Task NobodyAnsweringIsItselfAnAnswer()
    {
        await using var human = await JoinAsync("human");
        await using var scribe = new AskingAgent(AgentOptions) { Args = OneChoice };
        await scribe.StartAsync(_transport);

        await AskedAsync(human, () => human.SendMessageAsync("#main", "@scribe start").AsTask());

        // An agent holding its turn open forever is one that has quietly stopped working, so the
        // wait is bounded and the model is told plainly that nothing was decided.
        await UntilAsync(() => scribe.Answer is not null, "the agent waited past its own timeout");
        output.WriteLine(scribe.Answer!);
        Assert.Contains("Nobody answered", scribe.Answer!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AReplyNamesWhatItAnswers()
    {
        await using var human = await JoinAsync("human");
        await using var other = await JoinAsync("other");

        await human.SendMessageAsync("#main", "which of these should we do first?");
        var asked = await UntilMessageAsync(other, m => m.Sender == "human");

        await other.ReplyAsync("#main", "the second one", asked.MessageId!);
        var reply = await UntilMessageAsync(human, m => m.Sender == "other");

        // Without this an agent reading a busy room has to guess which of the last ten things
        // "the second one" is about.
        Assert.Equal(asked.MessageId, reply.ReplyTo);
    }

    private async Task<MsgPayload> UntilMessageAsync(BanterClient client, Func<MsgPayload, bool> predicate)
    {
        var deadline = DateTimeOffset.UtcNow + Patience;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var history = await client.GetHistoryAsync("#main", limit: 50);
            var hit = history.Messages.FirstOrDefault(predicate);
            if (hit is not null)
            {
                return hit;
            }

            await Task.Delay(25);
        }

        throw new Xunit.Sdk.XunitException("no such message arrived");
    }

    private static async Task UntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.Fail(what);
    }
}
