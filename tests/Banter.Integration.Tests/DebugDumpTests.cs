using System.Text.Json;
using Banter.App;
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
/// The debug bundle: what an admin hands a developer when "the delegator took ages and nothing
/// happened". It has to carry enough to answer that without a second round trip - the rooms and
/// their settings, who was in them and what they announced, what was said, and with a wire trace
/// running, the order it all arrived in.
/// </summary>
public sealed class DebugDumpTests(ITestOutputHelper output) : IAsyncLifetime
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private readonly TcpBanterTransport _transport = new();
    private readonly InMemoryAccountStore _accounts = new InMemoryAccountStore()
        .AddUser("root", "pw", isAgent: false, isAdmin: true)
        .AddUser("human", "pw")
        .AddUser("jade", "pw", isAgent: true);

    private string _root = null!;
    private BanterDatabase _database = null!;
    private WireTrace _trace = null!;
    private BanterServer _server = null!;

    public async Task InitializeAsync()
    {
        _root = Path.Combine(Path.GetTempPath(), $"banter-debug-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _database = new BanterDatabase(BanterStorageOptions.DefaultSqlite(Path.Combine(_root, "banter.db")));
        await _database.InitializeAsync();
        var files = new FileStore(_database, new FileStoreOptions { DataDirectory = Path.Combine(_root, "files") });
        _trace = WireTrace.Open(Path.Combine(_root, "trace.jsonl"));
        _server = new BanterServer(
            _transport, _accounts, new DbServerStore(_database), files,
            identities: new AgentIdentityStore(_database), trace: _trace);
        await _server.StartAsync(new Uri("tcp://127.0.0.1:0"));
    }

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        await _trace.DisposeAsync();
        BanterDatabase.ClearSqlitePools();
        Directory.Delete(_root, recursive: true);
    }

    private Task<BanterClient> ConnectAsync(string user) =>
        BanterClient.ConnectAsync(_transport, _server.Endpoint, user, "pw");

    [Fact]
    public async Task TheBundleCarriesTheRoomsTheSessionsAndWhatWasSaid()
    {
        await using var root = await ConnectAsync("root");
        await using var human = await ConnectAsync("human");
        await using var jade = await ConnectAsync("jade");
        await root.JoinAsync("#main");
        await human.JoinAsync("#main");
        await jade.AnnounceAgentAsync(new AgentAnnouncePayload(
            "jade", AgentLocality.Local, DataSensitivity.Sensitive, ["chat", "code"], "a test agent", Sdk: "9.9.9"));
        await jade.JoinAsync("#main");
        await human.SendMessageAsync("#main", "set up a project with merlin");
        await root.SetRoomParleyAsync("#main", RoomParley.Open);

        // Everything above has been written by the time the server answers a later request from
        // the same connection; the dump is such a request. The trace is written off-thread, so it
        // is the one thing given a moment.
        await Task.Delay(200);

        var json = await root.DebugDumpAsync().WaitAsync(Patience);
        output.WriteLine(json.Length > 4000 ? json[..4000] + "…" : json);
        using var doc = JsonDocument.Parse(json);
        var report = doc.RootElement;

        var server = report.GetProperty("Server");
        Assert.False(string.IsNullOrEmpty(server.GetProperty("Version").GetString()));
        Assert.Equal(_trace.Path, server.GetProperty("TraceFile").GetString());
        Assert.True(server.GetProperty("HasIdentityStore").GetBoolean());

        var main = report.GetProperty("Rooms").EnumerateArray().Single(r => r.GetProperty("Name").GetString() == "#main");
        Assert.Equal("Open", main.GetProperty("Parley").GetString());
        Assert.Equal("Sensitive", main.GetProperty("Sensitivity").GetString());
        Assert.Equal("jade", main.GetProperty("Delegator").GetString());
        Assert.Contains("human", main.GetProperty("Members").EnumerateArray().Select(m => m.GetString()));
        Assert.Contains(main.GetProperty("RecentMessages").EnumerateArray(),
            m => m.GetProperty("Text").GetString() == "set up a project with merlin");
        var agent = Assert.Single(main.GetProperty("Agents").EnumerateArray());
        Assert.Equal("jade", agent.GetProperty("Nick").GetString());

        var session = report.GetProperty("Sessions").EnumerateArray().Single(s => s.GetProperty("Nick").GetString() == "jade");
        Assert.True(session.GetProperty("IsAgent").GetBoolean());
        Assert.StartsWith("Banter.Client", session.GetProperty("Peer").GetString());
        Assert.Equal("9.9.9", session.GetProperty("Effective").GetProperty("Sdk").GetString());
        Assert.Contains("#main", session.GetProperty("Rooms").EnumerateArray().Select(r => r.GetString()));

        // The trace: the message, in order, with its text - which is what a developer reads first.
        var tail = report.GetProperty("TraceTail").EnumerateArray().Select(l => l.GetString()!).ToList();
        Assert.Contains(tail, l => l.Contains("\"type\":\"Msg\"", StringComparison.Ordinal)
                                    && l.Contains("set up a project with merlin", StringComparison.Ordinal));
        Assert.Contains(tail, l => l.Contains("\"type\":\"RoomDelegator\"", StringComparison.Ordinal));
    }

    /// <summary>
    /// From the client: through the head's save dialog when it has one, so on a phone the person
    /// picks a folder they can actually get to; into Downloads when it has none; and backing out
    /// of the dialog is not a bundle in Downloads either.
    /// </summary>
    [Fact]
    public async Task TheClientPutsTheBundleWhereThePersonChooses()
    {
        await using var root = await ConnectAsync("root");
        await root.JoinAsync("#main");
        var vm = new ChatViewModel();
        var picker = new StubFilePicker(null) { SavedAs = "Banter/banter-debug.json" };
        var session = new BanterChatSession(root, vm)
        {
            DownloadDirectory = Path.Combine(_root, "downloads"),
            SaveAs = BanterChatSession.SaverFrom(picker),
        };

        var said = await session.DebugBundleAsync().WaitAsync(Patience);
        Assert.StartsWith("Debug bundle saved to Banter/banter-debug.json", said);
        var (name, mime, content) = Assert.Single(picker.Saves);
        Assert.Matches(@"^banter-debug-\d{8}-\d{6}\.json$", name);
        Assert.Equal("application/json", mime);
        using (var doc = JsonDocument.Parse(content))
        {
            Assert.True(doc.RootElement.TryGetProperty("Rooms", out _));
        }

        Assert.False(Directory.Exists(Path.Combine(_root, "downloads")));

        // Backed out: nothing written anywhere, and said so.
        var cancelling = new StubFilePicker(null) { SavedAs = null };
        session.SaveAs = BanterChatSession.SaverFrom(cancelling);
        Assert.Equal("The bundle was not saved.", await session.DebugBundleAsync().WaitAsync(Patience));
        Assert.False(Directory.Exists(Path.Combine(_root, "downloads")));

        // No dialog on this head: Downloads, as before.
        session.SaveAs = null;
        said = await session.DebugBundleAsync().WaitAsync(Patience);
        var file = Assert.Single(Directory.GetFiles(Path.Combine(_root, "downloads")));
        Assert.Contains(file, said, StringComparison.Ordinal);

        // And a picker that cannot save is no saver at all.
        Assert.Null(BanterChatSession.SaverFrom(new StubFilePicker(null) { CanSave = false }));
    }

    [Fact]
    public async Task OnlyAnAdminMayTakeOne()
    {
        await using var human = await ConnectAsync("human");
        var refused = await Assert.ThrowsAsync<BanterErrorException>(() => human.DebugDumpAsync());
        Assert.Equal("NOT_ADMIN", refused.Code);
    }

    [Fact]
    public async Task ABundleShrinksItsHistoryToFitTheConnection()
    {
        await using var root = await ConnectAsync("root");
        await root.JoinAsync("#main");
        var big = new string('x', 60_000);
        for (var i = 0; i < 30; i++)
        {
            await root.SendMessageAsync("#main", $"{i}:{big}");
        }

        // 30 x 60 KB is past a TCP frame; the server halves the history until it fits rather
        // than refusing, and says in the bundle how much it kept.
        var json = await root.DebugDumpAsync(historyPerRoom: 100).WaitAsync(TimeSpan.FromSeconds(30));
        using var doc = JsonDocument.Parse(json);
        var main = doc.RootElement.GetProperty("Rooms").EnumerateArray().Single(r => r.GetProperty("Name").GetString() == "#main");
        var kept = main.GetProperty("RecentMessages").GetArrayLength();
        output.WriteLine($"kept {kept} of 30 messages in {json.Length / 1024} KB");
        Assert.InRange(kept, 0, 29);
    }
}
