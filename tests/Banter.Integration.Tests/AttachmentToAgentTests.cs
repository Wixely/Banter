using System.Net;
using System.Text;
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

namespace Banter.Integration.Tests;

/// <summary>
/// A file someone attaches reaches the agent that answers as the file it is - an image the model
/// can look at, a note saying a file is there - rather than as a filename nobody can open. And a
/// file goes out with the words about it, as one message.
/// </summary>
public sealed class AttachmentToAgentTests : IAsyncLifetime
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4];

    private readonly TcpBanterTransport _transport = new();
    private readonly InMemoryAccountStore _accounts = new InMemoryAccountStore()
        .AddUser("human", "pw")
        .AddUser("local", "pw", isAgent: true)
        .AddUser("claude", "pw", isAgent: true);

    private string _root = null!;
    private BanterDatabase _database = null!;
    private BanterServer _server = null!;

    /// <summary>A model endpoint that answers "seen" and keeps every request it was sent. Told to,
    /// it refuses anything with an image in it, the way a model without vision does.</summary>
    private sealed class RecordingModel(bool refuseImages = false) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            lock (Requests)
            {
                Requests.Add(body);
            }

            if (refuseImages && body.Contains("image_url", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent("""{"error":"this model does not support images"}"""),
                };
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
        _root = Path.Combine(Path.GetTempPath(), $"banter-attach-{Guid.NewGuid():N}");
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

    private BanterAgentOptions Agent(string nick, AgentLocality locality) => new()
    {
        Server = _server.Endpoint,
        User = nick,
        Password = "pw",
        Rooms = ["#main"],
        Locality = locality,
        Clearance = locality == AgentLocality.Local ? DataSensitivity.Sensitive : DataSensitivity.Public,
        Skills = ["chat"],
    };

    private static LlmChatAgentOptions Model => new() { Endpoint = new Uri("http://model.test/v1"), Model = "test" };

    private async Task<BanterClient> HumanAsync()
    {
        var human = await BanterClient.ConnectAsync(_transport, _server.Endpoint, "human", "pw");
        await human.JoinAsync("#main");
        return human;
    }

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

    private static async Task DelegatorAsync(BanterClient watcher, string nick)
    {
        var deadline = DateTimeOffset.UtcNow + Patience;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if ((await watcher.GetAgentsAsync("#main")).Agents.Any(a => a.IsDelegator && a.Nick == nick))
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.Fail($"{nick} never became delegator");
    }

    /// <summary>Uploads without an announcement and says something with the file on it.</summary>
    private static async Task SayWithFileAsync(BanterClient human, string text, string name, byte[] bytes, string mime)
    {
        var info = await human.UploadFileAsync("#main", name, bytes, mime, quiet: true);
        await human.SendMessageAsync("#main", text, info.FileId, replyTo: null, to: null);
    }

    [Fact]
    public async Task ALocalAgentIsShownTheImage()
    {
        await using var human = await HumanAsync();
        var model = new RecordingModel();
        await using var local = new LlmChatAgent(Agent("local", AgentLocality.Local), Model, model);
        await local.StartAsync(_transport);
        await DelegatorAsync(human, "local");

        await SayWithFileAsync(human, "what is in this?", "shot.png", Png, "image/png");

        await UntilAsync(() => model.Requests.Count > 0, "the model was never asked");
        var sent = model.Requests[^1];
        Assert.Contains("what is in this?", sent);
        Assert.Contains("An image is attached: shot.png", sent);
        Assert.Contains("\"image_url\"", sent);
        Assert.Contains("data:image/png;base64," + Convert.ToBase64String(Png), sent);
    }

    [Fact]
    public async Task ASmallTextFileIsShownAsItsText()
    {
        await using var human = await HumanAsync();
        var model = new RecordingModel();
        await using var local = new LlmChatAgent(Agent("local", AgentLocality.Local), Model, model);
        await local.StartAsync(_transport);
        await DelegatorAsync(human, "local");

        await SayWithFileAsync(human, "summarise this", "notes.txt", Encoding.UTF8.GetBytes("the plan is to ship on friday"), "text/plain");

        await UntilAsync(() => model.Requests.Count > 0, "the model was never asked");
        Assert.Contains("the plan is to ship on friday", model.Requests[^1]);
        Assert.DoesNotContain("image_url", model.Requests[^1]);
    }

    [Fact]
    public async Task AThirdPartyAgentIsToldOfTheFileButNotSentIt()
    {
        await using var human = await HumanAsync();
        var model = new RecordingModel();
        await using var claude = new LlmChatAgent(Agent("claude", AgentLocality.Frontier), Model, model);
        await claude.StartAsync(_transport);

        // Named, and classified public by its words - which is all the egress check can read.
        await SayWithFileAsync(human, "@claude search github for something public", "shot.png", Png, "image/png");

        await UntilAsync(() => model.Requests.Count > 0, "the model was never asked");
        var sent = model.Requests[^1];
        Assert.Contains("not shared with you", sent);
        Assert.DoesNotContain("image_url", sent);
        Assert.DoesNotContain(Convert.ToBase64String(Png), sent);
    }

    [Fact]
    public async Task AModelWithoutVisionStillAnswersTheWords()
    {
        await using var human = await HumanAsync();
        var model = new RecordingModel(refuseImages: true);
        await using var local = new LlmChatAgent(Agent("local", AgentLocality.Local), Model, model);
        await local.StartAsync(_transport);
        await DelegatorAsync(human, "local");

        await SayWithFileAsync(human, "what is in this?", "shot.png", Png, "image/png");

        await UntilAsync(() => model.Requests.Count >= 2, "the request was never retried without the image");
        Assert.Contains("cannot see images", model.Requests[1]);
        Assert.DoesNotContain("image_url", model.Requests[1]);

        var deadline = DateTimeOffset.UtcNow + Patience;
        while (DateTimeOffset.UtcNow < deadline
               && !(await human.GetHistoryAsync("#main", limit: 50)).Messages.Any(m => m.Sender == "local" && m.Text == "seen"))
        {
            await Task.Delay(25);
        }

        Assert.Contains((await human.GetHistoryAsync("#main", limit: 50)).Messages, m => m.Sender == "local" && m.Text == "seen");
    }

    /// <summary>An agent written against BanterAgent directly, as DaggerAgent is: whatever it is
    /// handed, it records, and answers "ok". With the four-argument form it is handed the file's id
    /// and fetches the file; on the three-argument form it is at least told what was attached.</summary>
    private sealed class PlainAgent(BanterAgentOptions options, bool newContract) : BanterAgent(options)
    {
        public string? Prompt { get; private set; }
        public Attachment? Attachment { get; private set; }

        protected override async IAsyncEnumerable<string> RespondAsync(
            string room, string sender, string prompt, string? fileId,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            if (!newContract)
            {
                await foreach (var piece in base.RespondAsync(room, sender, prompt, fileId, cancellationToken).ConfigureAwait(false))
                {
                    yield return piece;
                }

                yield break;
            }

            Prompt = prompt;
            Attachment = await FetchAttachmentAsync(fileId, cancellationToken).ConfigureAwait(false);
            yield return "ok";
        }

        protected override async IAsyncEnumerable<string> RespondAsync(
            string room, string sender, string prompt,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Prompt = prompt;
            await Task.Yield();
            yield return "ok";
        }
    }

    [Fact]
    public async Task AnAgentWrittenAgainstTheSdkIsHandedTheFile()
    {
        // Reported from a real room: a person attached a photo, and the agent - not Banter's own
        // LlmChatAgent - answered that only the filename had come through and the file was
        // nowhere in its workspace. The file was on the server; nothing had handed it over.
        await using var human = await HumanAsync();
        await using var local = new PlainAgent(Agent("local", AgentLocality.Local), newContract: true);
        await local.StartAsync(_transport);
        await DelegatorAsync(human, "local");

        await SayWithFileAsync(human, "what is in this?", "shot.png", Png, "image/png");

        await UntilAsync(() => local.Attachment is not null, "the agent was never handed the file");
        var file = local.Attachment!;
        Assert.Equal("shot.png", file.Name);
        Assert.Equal("image/png", file.MimeType);
        Assert.Equal(Png.Length, file.Size);
        Assert.Equal("data:image/png;base64," + Convert.ToBase64String(Png), file.ImageDataUri);
        Assert.Equal("what is in this?", local.Prompt);
    }

    [Fact]
    public async Task AnAgentOnTheOldContractIsAtLeastToldWhatWasAttached()
    {
        await using var human = await HumanAsync();
        await using var local = new PlainAgent(Agent("local", AgentLocality.Local), newContract: false);
        await local.StartAsync(_transport);
        await DelegatorAsync(human, "local");

        var info = await human.UploadFileAsync("#main", "shot.png", Png, "image/png", quiet: true);
        await human.SendMessageAsync("#main", "shot.png", info.FileId, replyTo: null, to: null);

        await UntilAsync(() => local.Prompt is not null, "the agent never answered");
        Assert.Contains("[Attached: shot.png (image/png, 12 B); file id " + info.FileId, local.Prompt, StringComparison.Ordinal);
    }

    /// <summary>An agent whose model calls whichever tools a test queues up.</summary>
    private sealed class ToolAgent(BanterAgentOptions options) : BanterAgent(options)
    {
        public Queue<(string Tool, string Args)> Plan { get; } = new();
        public List<string> Results { get; } = [];

        public IReadOnlyList<string> ToolNamesIn(string room) => [.. ToolsFor(room).Select(t => t.Name)];

        protected override async IAsyncEnumerable<string> RespondAsync(
            string room, string sender, string prompt,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            while (Plan.Count > 0)
            {
                var (tool, args) = Plan.Dequeue();
                var result = await InvokeToolAsync(tool, args, room, cancellationToken).ConfigureAwait(false);
                lock (Results)
                {
                    Results.Add(result.Content);
                }
            }

            yield return "ok";
        }
    }

    [Fact]
    public async Task AModelCanAskForARoomsFilesItself()
    {
        await using var human = await HumanAsync();
        await using var local = new ToolAgent(Agent("local", AgentLocality.Local));
        await local.StartAsync(_transport);
        await DelegatorAsync(human, "local");

        var notes = await human.UploadFileAsync("#main", "notes.txt", Encoding.UTF8.GetBytes("ship on friday"), "text/plain", quiet: true);
        await human.SendMessageAsync("#main", "notes.txt", notes.FileId, replyTo: null, to: null);

        // A later message refers to the file without attaching it again; the model goes and looks.
        await UntilAsync(() => local.ToolNamesIn("#main").Contains("read_file"), "the file tools were not offered");
        local.Plan.Enqueue(("room_files", "{}"));
        local.Plan.Enqueue(("read_file", $$"""{"file_id":"{{notes.FileId}}"}"""));
        await human.SendMessageAsync("#main", "what did the notes say?");

        await UntilAsync(() => local.Results.Count == 2, "the tools never ran");
        Assert.Contains("notes.txt (text/plain, 14 B) - file id " + notes.FileId, local.Results[0], StringComparison.Ordinal);
        Assert.Contains("ship on friday", local.Results[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFileAndItsWordsAreOneMessage()
    {
        await using var human = await HumanAsync();

        await SayWithFileAsync(human, "here is the chart", "chart.png", Png, "image/png");

        var deadline = DateTimeOffset.UtcNow + Patience;
        IReadOnlyList<MsgPayload> said = [];
        while (DateTimeOffset.UtcNow < deadline && said.Count == 0)
        {
            said = [.. (await human.GetHistoryAsync("#main", limit: 50)).Messages.Where(m => m.Sender == "human")];
            await Task.Delay(25);
        }

        var only = Assert.Single(said);
        Assert.Equal("here is the chart", only.Text);
        Assert.NotNull(only.FileId);
    }

    [Fact]
    public async Task AMessageCannotCarryAFileFromAnotherRoom()
    {
        await using var human = await HumanAsync();
        await human.JoinAsync("#other");
        var refused = new TaskCompletionSource<ErrorPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
        human.ServerError += e => refused.TrySetResult(e);

        var elsewhere = await human.UploadFileAsync("#other", "secret.png", Png, "image/png", quiet: true);
        await human.SendMessageAsync("#main", "look", elsewhere.FileId, replyTo: null, to: null);

        Assert.Equal("NO_ACCESS", (await refused.Task.WaitAsync(Patience)).Code);
        Assert.DoesNotContain((await human.GetHistoryAsync("#main", limit: 50)).Messages, m => m.Text == "look");
    }
}
