using System.Net;
using System.Text.Json;
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
/// A model that writes a tool call as text - "&lt;tool_call&gt;{…}&lt;/tool_call&gt;" in the reply,
/// which local models do constantly - gets the call made rather than the markup posted. Reported
/// as "garbage xml/tool call nonsense and nothing happens".
/// </summary>
public sealed class TextualToolCallTests(ITestOutputHelper output) : IAsyncLifetime
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private readonly TcpBanterTransport _transport = new();
    private readonly InMemoryAccountStore _accounts = new InMemoryAccountStore()
        .AddUser("human", "pw")
        .AddUser("local", "pw", isAgent: true);

    private string _root = null!;
    private BanterDatabase _database = null!;
    private BanterServer _server = null!;

    /// <summary>A model that answers each request with the next scripted reply, streamed in
    /// pieces the way a real one arrives, and keeps every request it was sent.</summary>
    private sealed class ScriptedModel(params string[] replies) : HttpMessageHandler
    {
        private readonly Queue<string> _replies = new(replies);
        public List<string> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            string reply;
            lock (Requests)
            {
                Requests.Add(body);
                reply = _replies.Count > 0 ? _replies.Dequeue() : "nothing more to say";
            }

            // In pieces of a few characters, so the hold-back logic sees the opener arrive the
            // way it does from a real stream rather than all at once.
            var chunks = Enumerable.Range(0, (reply.Length + 4) / 5).Select(i => reply.Substring(i * 5, Math.Min(5, reply.Length - (i * 5))));
            var sse = string.Concat(chunks.Select(c =>
                "data: " + JsonSerializer.Serialize(new { choices = new[] { new { delta = new { content = c } } } }) + "\n\n"))
                + "data: [DONE]\n\n";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sse) };
        }
    }

    public async Task InitializeAsync()
    {
        _root = Path.Combine(Path.GetTempPath(), $"banter-textcall-{Guid.NewGuid():N}");
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

    private BanterAgentOptions Agent() => new()
    {
        Server = _server.Endpoint,
        User = "local",
        Password = "pw",
        Rooms = ["#main"],
        Locality = AgentLocality.Local,
        Clearance = DataSensitivity.Sensitive,
        Skills = ["chat"],
    };

    private static async Task<IReadOnlyList<MsgPayload>> SaidByAsync(BanterClient human, string nick, int atLeast)
    {
        var deadline = DateTimeOffset.UtcNow + Patience;
        IReadOnlyList<MsgPayload> said = [];
        while (DateTimeOffset.UtcNow < deadline)
        {
            said = [.. (await human.GetHistoryAsync("#main", limit: 50)).Messages.Where(m => m.Sender == nick)];
            if (said.Count >= atLeast)
            {
                break;
            }

            await Task.Delay(25);
        }

        return said;
    }

    [Theory]
    [InlineData("<tool_call>{\"name\":\"who_is_here\",\"arguments\":{}}</tool_call>")]
    [InlineData("<tool_call>\n{\"name\": \"who_is_here\", \"arguments\": {}}\n</tool_call>")]
    [InlineData("```json\n{\"name\": \"who_is_here\", \"arguments\": {}}\n```")]
    [InlineData("<function=who_is_here>{}</function>")]
    [InlineData("{\"name\": \"who_is_here\", \"parameters\": {}}")]
    public async Task AToolCallWrittenAsTextIsMadeNotPosted(string written)
    {
        await using var human = await BanterClient.ConnectAsync(_transport, _server.Endpoint, "human", "pw");
        await human.JoinAsync("#main");
        var model = new ScriptedModel(written, "just me here, it seems");
        await using var local = new LlmChatAgent(Agent(),
            new LlmChatAgentOptions { Endpoint = new Uri("http://model.test/v1"), Model = "test", TraceDirectory = Path.Combine(_root, "trace") },
            model);
        await local.StartAsync(_transport);

        await human.SendMessageAsync("#main", "@local who else is here?");

        var said = await SaidByAsync(human, "local", 1);
        foreach (var m in said)
        {
            output.WriteLine(m.Text);
        }

        // The markup never reached the room; the tool ran and its answer went back to the model,
        // whose second reply is what people read.
        Assert.DoesNotContain(said, m => m.Text.Contains("tool_call", StringComparison.Ordinal) || m.Text.Contains("who_is_here", StringComparison.Ordinal));
        Assert.Contains(said, m => m.Text == "just me here, it seems");
        Assert.Equal(2, model.Requests.Count);
        Assert.Contains("No other agents are in this room", model.Requests[1], StringComparison.Ordinal);

        // And the trace a developer would read: one request and one response per call.
        var files = Directory.GetFiles(Path.Combine(_root, "trace"));
        Assert.Equal(2, files.Count(f => f.EndsWith("-request.json", StringComparison.Ordinal)));
        Assert.Equal(2, files.Count(f => f.EndsWith("-response.txt", StringComparison.Ordinal)));
        Assert.Contains("who_is_here", await File.ReadAllTextAsync(files.First(f => f.EndsWith("-request.json", StringComparison.Ordinal))), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProseThatMerelyBeginsLikeACallIsStillSaid()
    {
        await using var human = await BanterClient.ConnectAsync(_transport, _server.Endpoint, "human", "pw");
        await human.JoinAsync("#main");
        var model = new ScriptedModel("```\nsome code\n```\nand a sentence after it.", "unused");
        await using var local = new LlmChatAgent(Agent(), new LlmChatAgentOptions { Endpoint = new Uri("http://model.test/v1"), Model = "test" }, model);
        await local.StartAsync(_transport);

        await human.SendMessageAsync("#main", "@local show me");

        var said = await SaidByAsync(human, "local", 1);
        Assert.Equal("```\nsome code\n```\nand a sentence after it.", Assert.Single(said).Text);
        Assert.Single(model.Requests);
    }

    [Theory]
    [InlineData("<tool_call>{\"name\":\"a\",\"arguments\":{\"x\":1}}</tool_call>", "a", "{\"x\":1}")]
    [InlineData("<function=open_side_room>{\"purpose\":\"p\"}</function>", "open_side_room", "{\"purpose\":\"p\"}")]
    [InlineData("{\"function\":{\"name\":\"b\",\"arguments\":\"{\\\"k\\\":\\\"v\\\"}\"}}", "b", "{\"k\":\"v\"}")]
    [InlineData("{\"tool\":\"c\",\"args\":{}}", "c", "{}")]
    public void TheShapesModelsWriteAreRead(string text, string name, string arguments)
    {
        Assert.True(TextualToolCall.TryParse(text, out var n, out var a));
        Assert.Equal(name, n);
        Assert.Equal(arguments, a);
    }

    [Theory]
    [InlineData("Sure - let me check who is here.")]
    [InlineData("I would call <tool_call>{\"name\":\"a\"}</tool_call> but I will not.")]
    [InlineData("{\"notaname\": 1}")]
    [InlineData("```json\n[1,2,3]\n```")]
    public void ProseIsNotACall(string text) => Assert.False(TextualToolCall.TryParse(text, out _, out _));
}
