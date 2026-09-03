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
/// Adding, changing and removing an agent while the server runs — and the enrolment that gives a
/// machine its identity in the first place.
///
/// <para>The property the whole design rests on: <b>the agent's private key is made on its own
/// machine and never transmitted</b>. What crosses the wire at enrolment is a public key; what
/// crosses it at login is a signature over a nonce the server chose. So the enrolment code buys
/// exactly one registration, and a copy of the server's table lets nobody impersonate anybody.</para>
/// </summary>
public sealed class AgentIdentityTests(ITestOutputHelper output) : IAsyncLifetime
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private readonly TcpBanterTransport _transport = new();
    private readonly InMemoryAccountStore _accounts = new InMemoryAccountStore()
        .AddUser("root", "pw", isAgent: false, isAdmin: true)
        .AddUser("nell", "pw");

    private string _root = null!;
    private BanterDatabase _database = null!;
    private AgentIdentityStore _identities = null!;
    private BanterServer _server = null!;

    public async Task InitializeAsync()
    {
        _root = Path.Combine(Path.GetTempPath(), $"banter-ids-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _database = new BanterDatabase(BanterStorageOptions.DefaultSqlite(Path.Combine(_root, "banter.db")));
        await _database.InitializeAsync();
        _identities = new AgentIdentityStore(_database);
        var files = new FileStore(_database, new FileStoreOptions { DataDirectory = Path.Combine(_root, "files") });
        _server = new BanterServer(
            _transport, _accounts, new DbServerStore(_database), files, identities: _identities);
        await _server.StartAsync(new Uri("tcp://127.0.0.1:0"));
    }

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        BanterDatabase.ClearSqlitePools();
        Directory.Delete(_root, recursive: true);
    }

    private Task<BanterClient> AdminAsync() =>
        BanterClient.ConnectAsync(_transport, _server.Endpoint, "root", "pw");

    /// <summary>Creates an identity the way the admin page will, and returns the code it is handed.</summary>
    private static async Task<string> CreateAsync(BanterClient admin, string nick, string locality = "local", string clearance = "sensitive")
    {
        var reply = await admin.CreateAgentAsync(
            nick, ["#main"], ["chat"],
            Enum.Parse<AgentLocality>(locality, ignoreCase: true),
            Enum.Parse<DataSensitivity>(clearance, ignoreCase: true));
        return reply.Code;
    }

    [Fact]
    public async Task AnAdminCreatesAnAgentAndTheMachineEnrolsWithIt()
    {
        await using var admin = await AdminAsync();
        var code = await CreateAsync(admin, "scribe");
        output.WriteLine($"code: {code}");

        // The agent's machine. It makes its own key and sends only the public half.
        var (identity, privateKey) = await AgentEnrolment
            .EnrolAsync(_transport, _server.Endpoint, code)
            .WaitAsync(Patience);

        Assert.Equal("scribe", identity.Nick);
        Assert.True(identity.Enrolled);
        Assert.NotEmpty(identity.KeyFingerprint);

        // And it can now log in with the key instead of a password.
        await using var agent = await BanterClient
            .ConnectWithKeyAsync(_transport, _server.Endpoint, "scribe", privateKey)
            .WaitAsync(Patience);

        Assert.Equal("scribe", agent.Nick);
        Assert.True(agent.IsAgent);

        await agent.JoinAsync("#main");
        await agent.SendMessageAsync("#main", "reporting in");
        Assert.Equal("reporting in", (await agent.GetHistoryAsync("#main", limit: 5)).Messages[^1].Text);
    }

    [Fact]
    public async Task ACodeWorksOnceAndOnlyOnce()
    {
        await using var admin = await AdminAsync();
        var code = await CreateAsync(admin, "scribe");

        await AgentEnrolment.EnrolAsync(_transport, _server.Endpoint, code).WaitAsync(Patience);

        // A code left in a chat log, a clipboard or a screenshot is worth nothing once used — which
        // is the whole reason the password never travels.
        var second = await Assert.ThrowsAsync<InvalidOperationException>(
            () => AgentEnrolment.EnrolAsync(_transport, _server.Endpoint, code));

        output.WriteLine(second.Message);
        Assert.Contains("UNKNOWN_CODE", second.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAgentWithSomebodyElsesKeyIsRefused()
    {
        await using var admin = await AdminAsync();
        var scribeCode = await CreateAsync(admin, "scribe");
        var scoutCode = await CreateAsync(admin, "scout");

        var (_, scribeKey) = await AgentEnrolment.EnrolAsync(_transport, _server.Endpoint, scribeCode).WaitAsync(Patience);
        await AgentEnrolment.EnrolAsync(_transport, _server.Endpoint, scoutCode).WaitAsync(Patience);

        // Holding a valid key does not make you whoever you say you are.
        await Assert.ThrowsAsync<BanterAuthException>(
            () => BanterClient.ConnectWithKeyAsync(_transport, _server.Endpoint, "scout", scribeKey));
    }

    [Fact]
    public async Task DeletingAnAgentStopsItsKeyImmediately()
    {
        await using var admin = await AdminAsync();
        var code = await CreateAsync(admin, "scribe");
        var (_, privateKey) = await AgentEnrolment.EnrolAsync(_transport, _server.Endpoint, code).WaitAsync(Patience);

        await using (var agent = await BanterClient.ConnectWithKeyAsync(_transport, _server.Endpoint, "scribe", privateKey))
        {
            Assert.Equal("scribe", agent.Nick);
        }

        await admin.DeleteAgentAsync("scribe");

        // Not at midnight, not when a credential lapses: the server is the authority and is asked
        // every time, so removal takes effect on the very next attempt.
        await Assert.ThrowsAsync<BanterAuthException>(
            () => BanterClient.ConnectWithKeyAsync(_transport, _server.Endpoint, "scribe", privateKey));
    }

    [Fact]
    public async Task AnAgentIsEditedWhileTheServerRuns()
    {
        await using var admin = await AdminAsync();
        await CreateAsync(admin, "scribe", clearance: "sensitive");

        await admin.UpdateAgentAsync(
            "scribe", rooms: ["#main", "#notes"], skills: ["notes", "minutes"], clearance: DataSensitivity.Internal);

        var listed = await admin.ListAgentsAsync();
        var scribe = Assert.Single(listed, i => i.Nick == "scribe");

        Assert.Equal(["#main", "#notes"], scribe.Rooms);
        Assert.Equal(["notes", "minutes"], scribe.Skills);
        Assert.Equal("internal", scribe.Clearance);

        // Locality was not named, so it is left alone rather than reset to a default.
        Assert.Equal("local", scribe.Locality);
    }

    [Fact]
    public async Task ReissuingRetiresTheOldMachinesKey()
    {
        await using var admin = await AdminAsync();
        var first = await CreateAsync(admin, "scribe");
        var (_, oldKey) = await AgentEnrolment.EnrolAsync(_transport, _server.Endpoint, first).WaitAsync(Patience);

        var reissued = await admin.ReissueAgentAsync("scribe");
        var (_, newKey) = await AgentEnrolment.EnrolAsync(_transport, _server.Endpoint, reissued.Code).WaitAsync(Patience);

        // Reissue is what an operator reaches for when a laptop is lost, so it must not leave the
        // old one able to carry on.
        await Assert.ThrowsAsync<BanterAuthException>(
            () => BanterClient.ConnectWithKeyAsync(_transport, _server.Endpoint, "scribe", oldKey));

        await using var agent = await BanterClient
            .ConnectWithKeyAsync(_transport, _server.Endpoint, "scribe", newKey)
            .WaitAsync(Patience);
        Assert.Equal("scribe", agent.Nick);
    }

    [Fact]
    public async Task OnlyAnAdminMayManageIdentities()
    {
        await using var nell = await BanterClient.ConnectAsync(_transport, _server.Endpoint, "nell", "pw");

        // An agent that could create agents could give itself whatever clearance it liked, so this
        // is an operator decision and stays one.
        var refused = await Assert.ThrowsAsync<BanterErrorException>(
            () => nell.CreateAgentAsync("sneaky", ["#main"], ["chat"]));

        output.WriteLine(refused.Message);
        Assert.Contains("NOT_ADMIN", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A signature captured off the wire is worth nothing the second time.
    ///
    /// <para>This is what the two round trips buy. The server picks the nonce and forgets it the
    /// moment it is answered, so a replayed signature is answering a question nobody asked. Driven
    /// with raw frames because the client will not replay for you — which is exactly why the test
    /// has to.</para>
    /// </summary>
    [Fact]
    public async Task ACapturedSignatureCannotBeReplayed()
    {
        await using var admin = await AdminAsync();
        var code = await CreateAsync(admin, "scribe");
        var (_, privateKey) = await AgentEnrolment.EnrolAsync(_transport, _server.Endpoint, code).WaitAsync(Patience);

        var codec = new BanterCodec();

        async Task<(byte[] Nonce, object? Reply)> AttemptAsync(byte[]? replaySignature)
        {
            await using var connection = await _transport.ConnectAsync(_server.Endpoint);

            async Task<object?> RoundTripAsync(object payload)
            {
                var sent = codec.CreateEnvelope(payload);
                await connection.SendFrameAsync(codec.EncodeEnvelope(sent));
                while (await connection.ReceiveFrameAsync() is { } frame)
                {
                    var envelope = codec.DecodeEnvelope(frame);
                    if (envelope.ReplyTo == sent.MsgId)
                    {
                        return codec.DecodePayload(envelope);
                    }
                }

                throw new InvalidOperationException("no reply");
            }

            var issued = Assert.IsType<AuthChallengeIssuedPayload>(await RoundTripAsync(new AuthChallengePayload("scribe")));
            var signature = replaySignature
                ?? AgentKeys.Sign(privateKey, AgentKeys.ChallengeBytes("scribe", issued.Nonce));

            return (issued.Nonce, await RoundTripAsync(new AuthKeyPayload("scribe", signature)));
        }

        // A genuine attempt, and the signature it produced.
        var (firstNonce, firstReply) = await AttemptAsync(null);
        Assert.IsType<AuthOkPayload>(firstReply);
        var captured = AgentKeys.Sign(privateKey, AgentKeys.ChallengeBytes("scribe", firstNonce));

        // The same signature, offered against whatever nonce the server picks next.
        var (secondNonce, secondReply) = await AttemptAsync(captured);
        Assert.NotEqual(Convert.ToHexString(firstNonce), Convert.ToHexString(secondNonce));
        Assert.IsType<AuthFailPayload>(secondReply);
    }

    /// <summary>
    /// Answering the challenge wrongly does not leave it standing for another go: one challenge is
    /// spent by one attempt, however that attempt turns out.
    /// </summary>
    [Fact]
    public async Task AFailedAttemptBurnsItsChallenge()
    {
        await using var admin = await AdminAsync();
        var code = await CreateAsync(admin, "scribe");
        var (_, privateKey) = await AgentEnrolment.EnrolAsync(_transport, _server.Endpoint, code).WaitAsync(Patience);

        var codec = new BanterCodec();
        await using var connection = await _transport.ConnectAsync(_server.Endpoint);

        async Task<object?> RoundTripAsync(object payload)
        {
            var sent = codec.CreateEnvelope(payload);
            await connection.SendFrameAsync(codec.EncodeEnvelope(sent));
            while (await connection.ReceiveFrameAsync() is { } frame)
            {
                var envelope = codec.DecodeEnvelope(frame);
                if (envelope.ReplyTo == sent.MsgId)
                {
                    return codec.DecodePayload(envelope);
                }
            }

            throw new InvalidOperationException("no reply");
        }

        var issued = Assert.IsType<AuthChallengeIssuedPayload>(await RoundTripAsync(new AuthChallengePayload("scribe")));

        // Wrong first.
        Assert.IsType<AuthFailPayload>(await RoundTripAsync(new AuthKeyPayload("scribe", new byte[64])));

        // Now the right answer to that same nonce — which is no longer the question being asked.
        var correct = AgentKeys.Sign(privateKey, AgentKeys.ChallengeBytes("scribe", issued.Nonce));
        Assert.IsType<AuthFailPayload>(await RoundTripAsync(new AuthKeyPayload("scribe", correct)));
    }

    /// <summary>
    /// The whole point: a real agent, through the SDK every agent uses, running on a key rather
    /// than a password.
    ///
    /// <para>Without this the identity model would exist and nothing could reach it — the SDK is
    /// the only door an agent comes through, DaggerAgent included.</para>
    /// </summary>
    [Fact]
    public async Task AnAgentRunsOnItsKeyThroughTheSdk()
    {
        await using var admin = await AdminAsync();
        var code = await CreateAsync(admin, "scribe");
        var (_, privateKey) = await AgentEnrolment.EnrolAsync(_transport, _server.Endpoint, code).WaitAsync(Patience);

        await using var human = await BanterClient.ConnectAsync(_transport, _server.Endpoint, "nell", "pw");
        await human.JoinAsync("#main");

        await using var agent = new EchoAgent(new Banter.Agents.Sdk.BanterAgentOptions
        {
            Server = _server.Endpoint,
            User = "scribe",
            PrivateKey = privateKey,          // and no password at all
            Rooms = ["#main"],
            Locality = AgentLocality.Local,
            Clearance = DataSensitivity.Sensitive,
            Skills = ["chat"],
        });

        await agent.StartAsync(_transport).WaitAsync(Patience);
        await human.SendMessageAsync("#main", "@scribe are you there?");

        var deadline = DateTimeOffset.UtcNow + Patience;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var history = await human.GetHistoryAsync("#main", limit: 50);
            if (history.Messages.Any(m => m.Sender == "scribe"))
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.Fail("the keyed agent never answered");
    }

    private sealed class EchoAgent(Banter.Agents.Sdk.BanterAgentOptions options)
        : Banter.Agents.Sdk.BanterAgent(options)
    {
        protected override async IAsyncEnumerable<string> RespondAsync(
            string room, string sender, string prompt,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            yield return "here";
        }
    }

    /// <summary>
    /// The whole loop, the way an operator walks it: an admin mints a code, the agent's machine
    /// redeems it and keeps the key on disk, and the agent then runs from that file with no
    /// password anywhere.
    /// </summary>
    [Fact]
    public async Task AKeyOnDiskIsEnoughToRunAnAgent()
    {
        await using var admin = await AdminAsync();
        var code = await CreateAsync(admin, "scribe");

        var keyPath = Path.Combine(_root, "keys", "scribe.key");
        var (identity, privateKey) = await AgentEnrolment
            .EnrolAsync(_transport, _server.Endpoint, code)
            .WaitAsync(Patience);
        await AgentKeyFile.SaveAsync(keyPath, privateKey);

        Assert.True(AgentKeyFile.IsUsable(keyPath));
        Assert.Equal("scribe", identity.Nick);

        // Read back from disk, exactly as the fleet supervisor does.
        var fromDisk = await AgentKeyFile.LoadAsync(keyPath);

        await using var agent = new EchoAgent(new Banter.Agents.Sdk.BanterAgentOptions
        {
            Server = _server.Endpoint,
            User = "scribe",
            PrivateKey = fromDisk,
            Rooms = ["#main"],
            Locality = AgentLocality.Local,
            Clearance = DataSensitivity.Sensitive,
            Skills = ["chat"],
        });

        await agent.StartAsync(_transport).WaitAsync(Patience);

        // It is really in the room, not merely connected: a human sees it arrive.
        await using var human = await BanterClient.ConnectAsync(_transport, _server.Endpoint, "nell", "pw");
        await human.JoinAsync("#main");
        await human.SendMessageAsync("#main", "@scribe hello");

        var deadline = DateTimeOffset.UtcNow + Patience;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if ((await human.GetHistoryAsync("#main", limit: 50)).Messages.Any(m => m.Sender == "scribe"))
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.Fail("the agent running from a key file never answered");
    }

    /// <summary>
    /// A file that is not a key is reported as such, before anything tries to connect — otherwise
    /// a truncated or empty file looks like a login failure and sends somebody to the server logs.
    /// </summary>
    [Fact]
    public async Task RubbishInTheKeyFileIsCalledOutAsRubbish()
    {
        var keyPath = Path.Combine(_root, "keys", "broken.key");
        Directory.CreateDirectory(Path.GetDirectoryName(keyPath)!);
        await File.WriteAllTextAsync(keyPath, "not a key");

        Assert.False(AgentKeyFile.IsUsable(keyPath));
        Assert.False(AgentKeyFile.IsUsable(Path.Combine(_root, "keys", "absent.key")));
    }

    [Fact]
    public async Task TheServerNeverLearnsThePrivateKey()
    {
        await using var admin = await AdminAsync();
        var code = await CreateAsync(admin, "scribe");
        var (_, privateKey) = await AgentEnrolment.EnrolAsync(_transport, _server.Endpoint, code).WaitAsync(Patience);

        var stored = await _identities.FindAsync("scribe");
        Assert.NotNull(stored);
        Assert.NotNull(stored!.PublicKey);

        // The private key exists only on the agent's machine. Everything the server holds is
        // public, which is why a stolen database is not a stolen identity — searched as raw bytes
        // rather than trusted to a property name.
        var everythingStored = stored.PublicKey!.Concat(System.Text.Encoding.UTF8.GetBytes(stored.KeyFingerprint)).ToArray();
        Assert.DoesNotContain(Convert.ToHexString(privateKey), Convert.ToHexString(everythingStored), StringComparison.Ordinal);
    }
}
