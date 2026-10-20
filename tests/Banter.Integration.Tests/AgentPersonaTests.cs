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
/// Who an agent is, as prompt text. The agent can say so when it starts, and an admin can say so
/// on its identity from the agents page - which wins, and reaches the model on the very next turn
/// rather than at the next restart.
/// </summary>
public sealed class AgentPersonaTests(ITestOutputHelper output) : IAsyncLifetime
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private readonly TcpBanterTransport _transport = new();
    private readonly InMemoryAccountStore _accounts = new InMemoryAccountStore()
        .AddUser("root", "pw", isAgent: false, isAdmin: true)
        .AddUser("human", "pw")
        .AddUser("local", "pw", isAgent: true);

    private string _root = null!;
    private BanterDatabase _database = null!;
    private BanterServer _server = null!;

    /// <summary>A model endpoint that answers "seen" and keeps every request it was sent.</summary>
    private sealed class RecordingModel : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        /// <summary>The system prompt of the request at <paramref name="index"/>, as the model saw it.</summary>
        public string SystemPromptOf(int index)
        {
            string body;
            lock (Requests)
            {
                body = Requests[index];
            }

            using var json = JsonDocument.Parse(body);
            var first = json.RootElement.GetProperty("messages")[0];
            Assert.Equal("system", first.GetProperty("role").GetString());
            return first.GetProperty("content").GetString() ?? "";
        }

        public int Count
        {
            get
            {
                lock (Requests)
                {
                    return Requests.Count;
                }
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            lock (Requests)
            {
                Requests.Add(body);
            }

            var chunk = JsonSerializer.Serialize(new { choices = new[] { new { delta = new { content = "seen" } } } });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($"data: {chunk}\n\ndata: [DONE]\n\n"),
            };
        }
    }

    public async Task InitializeAsync()
    {
        _root = Path.Combine(Path.GetTempPath(), $"banter-persona-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _database = new BanterDatabase(BanterStorageOptions.DefaultSqlite(Path.Combine(_root, "banter.db")));
        await _database.InitializeAsync();
        var files = new FileStore(_database, new FileStoreOptions { DataDirectory = Path.Combine(_root, "files") });
        _server = new BanterServer(
            _transport, _accounts, new DbServerStore(_database), files,
            identities: new AgentIdentityStore(_database));
        await _server.StartAsync(new Uri("tcp://127.0.0.1:0"));
    }

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        BanterDatabase.ClearSqlitePools();
        Directory.Delete(_root, recursive: true);
    }

    private BanterAgentOptions Agent(string persona) => new()
    {
        Server = _server.Endpoint,
        User = "local",
        Password = "pw",
        Rooms = ["#main"],
        Locality = AgentLocality.Local,
        Clearance = DataSensitivity.Sensitive,
        Skills = ["chat"],
        Persona = persona,
    };

    private static LlmChatAgentOptions Model => new() { Endpoint = new Uri("http://model.test/v1"), Model = "test" };

    private static async Task UntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTimeOffset.UtcNow + Patience;
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

    [Fact]
    public async Task TheAgentsOwnPersonaFollowsTheSystemPrompt()
    {
        await using var human = await BanterClient.ConnectAsync(_transport, _server.Endpoint, "human", "pw");
        await human.JoinAsync("#main");

        var model = new RecordingModel();
        await using var local = new LlmChatAgent(Agent("You are dry and brief, and you never use exclamation marks."), Model, model);
        await local.StartAsync(_transport);

        await human.SendMessageAsync("#main", "@local hello");
        await UntilAsync(() => model.Count == 1, "the model was never asked");

        var system = model.SystemPromptOf(0);
        output.WriteLine(system);

        // After the general instructions, so it can override their manner, and separate from
        // them, so the two read as what they are rather than as one run-on sentence.
        Assert.StartsWith(Model.SystemPrompt, system, StringComparison.Ordinal);
        Assert.EndsWith("\n\nYou are dry and brief, and you never use exclamation marks.", system, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAdminsPersonaOutranksTheAgentsAndReachesTheNextTurn()
    {
        await using var admin = await BanterClient.ConnectAsync(_transport, _server.Endpoint, "root", "pw");
        await using var human = await BanterClient.ConnectAsync(_transport, _server.Endpoint, "human", "pw");
        await human.JoinAsync("#main");

        var model = new RecordingModel();
        await using var local = new LlmChatAgent(Agent("Plain and helpful."), Model, model);
        await local.StartAsync(_transport);

        await human.SendMessageAsync("#main", "@local hello");
        await UntilAsync(() => model.Count == 1, "the model was never asked");
        Assert.EndsWith("Plain and helpful.", model.SystemPromptOf(0), StringComparison.Ordinal);

        // The operator gives it a character from the agents page, while it runs. Creating the
        // identity is how the page does it; the enrolment code it mints is beside the point here.
        await admin.CreateAgentAsync("local", ["#main"], ["chat"], persona: "You answer as a weary pirate.").WaitAsync(Patience);
        await UntilAsync(() => local.EffectiveAttributes?.Persona == "You answer as a weary pirate.",
            "the live agent was never told its new persona");

        await human.SendMessageAsync("#main", "@local hello again");
        await UntilAsync(() => model.Count == 2, "the model was not asked a second time");
        var granted = model.SystemPromptOf(1);
        output.WriteLine(granted);
        Assert.EndsWith("You answer as a weary pirate.", granted, StringComparison.Ordinal);
        Assert.DoesNotContain("Plain and helpful.", granted, StringComparison.Ordinal);

        // The page lists it, and an emptied box hands the choice back to the agent.
        var listed = Assert.Single(await admin.ListAgentsAsync().WaitAsync(Patience));
        Assert.Equal("You answer as a weary pirate.", listed.Persona);

        await admin.UpdateAgentAsync("local", persona: "").WaitAsync(Patience);
        await UntilAsync(() => local.EffectiveAttributes?.Persona == "Plain and helpful.",
            "clearing the persona did not hand the choice back to the agent");
        Assert.Equal("", Assert.Single(await admin.ListAgentsAsync().WaitAsync(Patience)).Persona);

        await human.SendMessageAsync("#main", "@local one more");
        await UntilAsync(() => model.Count == 3, "the model was not asked a third time");
        Assert.EndsWith("Plain and helpful.", model.SystemPromptOf(2), StringComparison.Ordinal);
    }
}
