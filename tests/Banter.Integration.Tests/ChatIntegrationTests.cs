using Banter.Client.Core;
using Banter.Core;
using Banter.Protocol;
using Banter.Protocol.Transport;
using Banter.Server;
using Banter.Server.Persistence;
using Xunit;

namespace Banter.Integration.Tests;

/// <summary>In-proc server + real clients over loopback TCP, on a real SQLite database —
/// the PLAN Phase 1 exit criteria.</summary>
public sealed class ChatIntegrationTests : IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private readonly TcpBanterTransport _transport = new();
    private readonly InMemoryAccountStore _accounts = new InMemoryAccountStore()
        .AddUser("alice", "pw-a")
        .AddUser("bob", "pw-b")
        .AddUser("dagger", "pw-d", isAgent: true);
    private string _dbPath = null!;
    private string _dataDir = null!;
    private BanterDatabase _database = null!;
    private Banter.Server.Files.FileStore _files = null!;
    private BanterServer _server = null!;

    public async Task InitializeAsync()
    {
        var id = Guid.NewGuid().ToString("N");
        _dbPath = Path.Combine(Path.GetTempPath(), $"banter-it-{id}.db");
        _dataDir = Path.Combine(Path.GetTempPath(), $"banter-it-files-{id}");
        _database = new BanterDatabase(BanterStorageOptions.DefaultSqlite(_dbPath));
        await _database.InitializeAsync();
        _files = new Banter.Server.Files.FileStore(_database, new Banter.Server.Files.FileStoreOptions { DataDirectory = _dataDir });
        _server = new BanterServer(_transport, _accounts, new DbServerStore(_database), _files);
        await _server.StartAsync(new Uri("tcp://127.0.0.1:0"));
    }

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        BanterDatabase.ClearSqlitePools();
        File.Delete(_dbPath);
        if (Directory.Exists(_dataDir))
        {
            Directory.Delete(_dataDir, recursive: true);
        }
    }

    [Fact]
    public async Task ClientReconnectsAndRejoinsAfterServerRestart()
    {
        var port = _server.Endpoint.Port;
        await using var alice = await ConnectAsync("alice", "pw-a");
        await alice.JoinAsync("#comeback");

        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        alice.Disconnected += () => disconnected.TrySetResult();
        alice.Reconnected += () => reconnected.TrySetResult();

        await _server.DisposeAsync();
        await disconnected.Task.WaitAsync(Timeout);

        // Bring a new server up on the same port; the client should redial, re-auth, rejoin.
        _server = new BanterServer(_transport, _accounts, new DbServerStore(_database), _files);
        await StartOnPortWithRetryAsync(_server, port);
        await reconnected.Task.WaitAsync(Timeout);

        // Prove the rejoin is real on the server side: a message reaches alice in #comeback.
        var aliceSees = Expect<MsgPayload>(handler => alice.MessageReceived += handler);
        await using var bob = await ConnectAsync("bob", "pw-b");
        await bob.JoinAsync("#comeback");
        await bob.SendMessageAsync("#comeback", "welcome back");
        Assert.Equal("welcome back", (await aliceSees.Task.WaitAsync(Timeout)).Text);
    }

    private static async Task StartOnPortWithRetryAsync(BanterServer server, int port)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await server.StartAsync(new Uri($"tcp://127.0.0.1:{port}"));
                return;
            }
            catch (System.Net.Sockets.SocketException) when (attempt < 20)
            {
                await Task.Delay(100);
            }
        }
    }

    [Fact]
    public async Task HistoryAndTopicSurviveAServerRestart()
    {
        await using (var alice = await ConnectAsync("alice", "pw-a"))
        {
            await alice.JoinAsync("#durable");
            await alice.SetTopicAsync("#durable", "here to stay");
            await alice.SendMessageAsync("#durable", "before the restart");
            await WaitForHistoryCountAsync(alice, "#durable", 1);
        }

        await _server.DisposeAsync();

        // A new server process over the same database.
        _server = new BanterServer(_transport, new InMemoryAccountStore().AddUser("bob", "pw-b"), new DbServerStore(_database), _files);
        await _server.StartAsync(new Uri("tcp://127.0.0.1:0"));

        await using var bob = await ConnectAsync("bob", "pw-b");
        var rooms = await bob.ListRoomsAsync();
        var durable = Assert.Single(rooms.Rooms, r => r.Name == "#durable");
        Assert.Equal("here to stay", durable.Topic);

        await bob.JoinAsync("#durable");
        var history = await bob.GetHistoryAsync("#durable");
        var survivor = Assert.Single(history.Messages);
        Assert.Equal("before the restart", survivor.Text);
        Assert.Equal("alice", survivor.Sender);
    }

    private Task<BanterClient> ConnectAsync(string user, string secret) =>
        BanterClient.ConnectAsync(_transport, _server.Endpoint, user, secret);

    [Fact]
    public async Task TwoClientsChatInARoom()
    {
        await using var alice = await ConnectAsync("alice", "pw-a");
        await using var bob = await ConnectAsync("bob", "pw-b");

        var bobSees = Expect<MsgPayload>(handler => bob.MessageReceived += handler);
        var aliceSees = Expect<MsgPayload>(handler => alice.MessageReceived += handler);

        await alice.JoinAsync("#main");
        await bob.JoinAsync("#main");
        await alice.SendMessageAsync("#main", "hello agents");

        var received = await bobSees.Task.WaitAsync(Timeout);
        Assert.Equal("#main", received.Room);
        Assert.Equal("alice", received.Sender);
        Assert.Equal("hello agents", received.Text);
        Assert.False(string.IsNullOrEmpty(received.MessageId));
        Assert.True(received.Timestamp > 0);

        // The sender gets the same authoritative echo.
        var echoed = await aliceSees.Task.WaitAsync(Timeout);
        Assert.Equal(received.MessageId, echoed.MessageId);
    }

    [Fact]
    public async Task SenderIsAuthoritativeFromServerNotClient()
    {
        await using var alice = await ConnectAsync("alice", "pw-a");
        await using var bob = await ConnectAsync("bob", "pw-b");
        await alice.JoinAsync("#spoof");
        await bob.JoinAsync("#spoof");

        var bobSees = Expect<MsgPayload>(handler => bob.MessageReceived += handler);

        // Client.SendMessageAsync uses its own nick, so forge a frame directly at the protocol level.
        var codec = new BanterCodec();
        var forged = codec.CreateEnvelope(new MsgPayload("#spoof", "bob", "I am totally bob", 999, null, "forged-id"));
        // Reach in via a raw connection: connect, auth as alice, join, send forged payload.
        var raw = await _transport.ConnectAsync(_server.Endpoint);
        await using var _ = raw;
        await SendAsync(raw, codec, new HelloPayload("raw", "0", []));
        Assert.NotNull(await ReceiveAsync(raw, codec));
        await SendAsync(raw, codec, new AuthPayload("alice", "pw-a", false));
        Assert.IsType<AuthOkPayload>(await ReceiveAsync(raw, codec));
        await SendAsync(raw, codec, new JoinPayload("#spoof"));
        Assert.IsType<OkPayload>(await ReceiveAsync(raw, codec));
        await raw.SendFrameAsync(codec.EncodeEnvelope(forged));

        var seen = await bobSees.Task.WaitAsync(Timeout);
        Assert.Equal("alice", seen.Sender);
        Assert.NotEqual("forged-id", seen.MessageId);
        Assert.NotEqual(999, seen.Timestamp);
    }

    [Fact]
    public async Task HistoryReplaysForALateJoiner()
    {
        await using var alice = await ConnectAsync("alice", "pw-a");
        await alice.JoinAsync("#history");

        var sent = new List<string>();
        for (var i = 1; i <= 3; i++)
        {
            var text = $"message {i}";
            sent.Add(text);
            await alice.SendMessageAsync("#history", text);
        }

        // The echo of the last message confirms all three are in history (single-writer ordering).
        await WaitForHistoryCountAsync(alice, "#history", 3);

        await using var bob = await ConnectAsync("bob", "pw-b");
        await bob.JoinAsync("#history");

        var page = await bob.GetHistoryAsync("#history", limit: 2);
        Assert.Equal(sent[^2..], page.Messages.Select(m => m.Text).ToList());
        Assert.NotNull(page.NextCursor);

        var older = await bob.GetHistoryAsync("#history", beforeMessageId: page.NextCursor);
        Assert.Equal([sent[0]], older.Messages.Select(m => m.Text).ToList());
        Assert.Null(older.NextCursor);
    }

    [Fact]
    public async Task BadCredentialsAreRejected()
    {
        var ex = await Assert.ThrowsAsync<BanterAuthException>(() => ConnectAsync("alice", "wrong"));
        Assert.Contains("Invalid credentials", ex.Message);
    }

    [Fact]
    public async Task UnauthenticatedRoomVerbsAreRefused()
    {
        var codec = new BanterCodec();
        await using var raw = await _transport.ConnectAsync(_server.Endpoint);
        await SendAsync(raw, codec, new JoinPayload("#main"));
        var reply = await ReceiveAsync(raw, codec);
        var error = Assert.IsType<ErrorPayload>(reply);
        Assert.Equal("UNAUTHENTICATED", error.Code);
    }

    [Fact]
    public async Task RoomListAndMembersReflectReality()
    {
        await using var alice = await ConnectAsync("alice", "pw-a");
        await using var dagger = await ConnectAsync("dagger", "pw-d");
        await alice.JoinAsync("#lobby");
        await dagger.JoinAsync("#lobby");
        await alice.SetTopicAsync("#lobby", "the lobby");

        // Topic is a broadcast; wait until it lands before asserting the listing.
        var topicSeen = Expect<TopicPayload>(handler => alice.TopicChanged += handler);
        await topicSeen.Task.WaitAsync(Timeout);

        var rooms = await alice.ListRoomsAsync();
        var lobby = Assert.Single(rooms.Rooms, r => r.Name == "#lobby");
        Assert.Equal("the lobby", lobby.Topic);
        Assert.Equal(2, lobby.MemberCount);

        var members = await alice.GetMembersAsync("#lobby");
        Assert.Equal(2, members.Members.Count);
        var agent = Assert.Single(members.Members, m => m.Nick == "dagger");
        Assert.True(agent.IsAgent);
    }

    [Fact]
    public async Task JoinAndPartAreAnnounced()
    {
        await using var alice = await ConnectAsync("alice", "pw-a");
        await alice.JoinAsync("#door");

        var joinSeen = Expect<JoinPayload>(handler => alice.MemberJoined += handler, j => j.Nick == "bob");
        var bob = await ConnectAsync("bob", "pw-b");
        await bob.JoinAsync("#door");
        Assert.Equal("#door", (await joinSeen.Task.WaitAsync(Timeout)).Room);

        var partSeen = Expect<PartPayload>(handler => alice.MemberParted += handler, p => p.Nick == "bob");
        await bob.DisposeAsync();
        var part = await partSeen.Task.WaitAsync(Timeout);
        Assert.Equal("#door", part.Room);
    }

    [Fact]
    public async Task PrivateMessagesRouteToEverySessionOfTheRecipient()
    {
        await using var alice = await ConnectAsync("alice", "pw-a");
        await using var bobPhone = await ConnectAsync("bob", "pw-b");
        await using var bobDesktop = await ConnectAsync("bob", "pw-b");

        var phoneSees = Expect<PrivMsgPayload>(handler => bobPhone.PrivateMessageReceived += handler);
        var desktopSees = Expect<PrivMsgPayload>(handler => bobDesktop.PrivateMessageReceived += handler);

        await alice.SendPrivateMessageAsync("bob", "psst");

        foreach (var seen in new[] { await phoneSees.Task.WaitAsync(Timeout), await desktopSees.Task.WaitAsync(Timeout) })
        {
            Assert.Equal("alice", seen.Sender);
            Assert.Equal("psst", seen.Text);
            Assert.True(seen.Timestamp > 0);
        }
    }

    [Fact]
    public async Task PrivateMessageToOfflineUserErrors()
    {
        await using var alice = await ConnectAsync("alice", "pw-a");
        var ex = await Assert.ThrowsAsync<BanterErrorException>(() => alice.SendPrivateMessageAsync("dagger", "anyone home?"));
        Assert.Equal("NO_SUCH_USER", ex.Code);
    }

    [Fact]
    public async Task InvalidRoomNameIsRejected()
    {
        await using var alice = await ConnectAsync("alice", "pw-a");
        var ex = await Assert.ThrowsAsync<BanterErrorException>(() => alice.JoinAsync("no-hash"));
        Assert.Equal("BAD_ROOM", ex.Code);
    }

    private static TaskCompletionSource<T> Expect<T>(Action<Action<T>> subscribe, Func<T, bool>? filter = null)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        subscribe(item =>
        {
            if (filter is null || filter(item))
            {
                tcs.TrySetResult(item);
            }
        });
        return tcs;
    }

    private static async Task WaitForHistoryCountAsync(BanterClient client, string room, int count)
    {
        var deadline = DateTimeOffset.UtcNow + Timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var page = await client.GetHistoryAsync(room, limit: 100);
            if (page.Messages.Count >= count)
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.Fail($"History never reached {count} messages in {room}.");
    }

    private static ValueTask SendAsync(IBanterConnection connection, BanterCodec codec, object payload) =>
        connection.SendFrameAsync(codec.EncodeEnvelope(codec.CreateEnvelope(payload)));

    /// <summary>Reads frames until a correlated reply arrives, skipping broadcast pushes
    /// (which have no <c>replyTo</c>).</summary>
    private static async Task<object?> ReceiveAsync(IBanterConnection connection, BanterCodec codec)
    {
        var deadline = DateTimeOffset.UtcNow + Timeout;
        while (true)
        {
            var remaining = deadline - DateTimeOffset.UtcNow;
            var frame = await connection.ReceiveFrameAsync().AsTask().WaitAsync(remaining);
            Assert.NotNull(frame);
            var envelope = codec.DecodeEnvelope(frame);
            if (envelope.ReplyTo is not null)
            {
                return codec.DecodePayload(envelope);
            }
        }
    }
}
