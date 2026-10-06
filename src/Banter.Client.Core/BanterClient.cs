using System.Collections.Concurrent;
using Banter.Protocol;
using Banter.Protocol.Transport;

namespace Banter.Client.Core;

public sealed record BanterClientOptions
{
    public string ClientName { get; init; } = "Banter.Client";
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>When the connection drops, redial + re-auth + rejoin rooms automatically.
    /// Initial connection failures still throw — reconnect only guards an established session.</summary>
    public bool AutoReconnect { get; init; } = true;

    public TimeSpan ReconnectInitialDelay { get; init; } = TimeSpan.FromMilliseconds(250);
    public TimeSpan ReconnectMaxDelay { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long something being sent will wait for a redial to finish before giving up.
    ///
    /// <para>A reconnect is not an error — it is the ordinary state of a phone, which loses its
    /// sockets whenever the platform decides the app has been out of the foreground long enough
    /// (measured on Android: every socket destroyed after ~30-45s backgrounded). Without a wait
    /// here, whatever the person did first on returning — a message, an attachment — was thrown
    /// away against a connection that was already being replaced, and was gone a second before
    /// the client finished reconnecting.</para>
    ///
    /// <para>The wait is for the session to be <em>usable</em>, not merely dialled: rooms are
    /// rejoined on a background task after a redial, and the server rejects a message to a room
    /// this session has not joined yet, so a send released too early is lost just as surely.</para>
    ///
    /// <para><see cref="TimeSpan.Zero"/> restores the old behaviour of failing immediately.</para>
    /// </summary>
    public TimeSpan ReconnectGrace { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How often to ping the server when nothing else is being said. <see cref="TimeSpan.Zero"/>
    /// turns it off.
    ///
    /// <para>Chat is mostly silence, and silence is indistinguishable from a connection that has
    /// died — so a client that never speaks unprompted finds out it was disconnected only when
    /// someone tries to type. Worse, a CupriNet node closes a Pilgrimage that has gone quiet for
    /// five minutes (measured, and not configurable through Nodestar), which for a room nobody
    /// has spoken in is the normal case rather than the exceptional one.</para>
    ///
    /// <para>Two minutes leaves room for a missed one inside that five, and costs a round trip
    /// per client per two minutes. The reply is what proves the path is alive in both
    /// directions; the request alone would only prove we can still write.</para>
    /// </summary>
    public TimeSpan KeepAliveInterval { get; init; } = TimeSpan.FromMinutes(2);
}

/// <summary>
/// The client runtime: connect + handshake + auth, request/response correlation over
/// <c>msgId</c>/<c>replyTo</c>, server pushes surfaced as events, and automatic reconnect with
/// exponential backoff and room rejoin. UI layers (CLI, CupriFace app) and the agent SDK all
/// sit on this.
/// </summary>
public sealed partial class BanterClient : IAsyncDisposable
{
    private readonly IBanterClientTransport _transport;
    private readonly Uri _endpoint;
    private readonly string _username;
    private readonly string _secret;

    /// <summary>
    /// This machine's private key when the account is an enrolled agent, null when it authenticates
    /// with a password. Set once at connect and never sent — see <see cref="AuthenticateWithKeyAsync"/>.
    /// </summary>
    private readonly byte[]? _privateKey;
    private readonly BanterClientOptions _options;
    private readonly BanterCodec _codec = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<object?>> _pending = new();
    private readonly HashSet<string> _joinedRooms = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _roomsLock = new();
    private readonly CancellationTokenSource _lifecycle = new();
    private volatile IBanterConnection? _connection;

    /// <summary>
    /// Completed while this session can carry traffic, and replaced with a fresh incomplete
    /// source the moment the connection drops. Anything being sent waits on it, so a redial is
    /// a pause rather than a hole (see <see cref="BanterClientOptions.ReconnectGrace"/>).
    ///
    /// <para>Completed after the rooms are rejoined rather than when the socket is up: the
    /// server rejects a message to a room this session has not joined, so releasing sends at
    /// the redial would swap one silent loss for another.</para>
    /// </summary>
    /// <para>Read through <see cref="Volatile"/> and swapped by compare-and-exchange rather than
    /// marked volatile: the receive loop and a thread that just failed a write can both find the
    /// connection dead at once, and two plain swaps would leave one of them holding a source
    /// nothing will ever complete.</para>
    private TaskCompletionSource _ready =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private Task? _sessionLoop;
    private Task? _keepAliveLoop;
    private bool _disposed;

    private BanterClient(
        IBanterClientTransport transport, Uri endpoint, string username, string secret,
        BanterClientOptions options, byte[]? privateKey = null)
    {
        _transport = transport;
        _endpoint = endpoint;
        _username = username;
        _secret = secret;
        _options = options;
        _privateKey = privateKey;
    }

    public string Nick { get; private set; } = "";
    public bool IsAgent { get; private set; }

    /// <summary>Whether this account may run operator actions — agent identities, above all.</summary>
    public bool IsAdmin { get; private set; }
    public string SessionId { get; private set; } = "";
    /// <summary>The banter.core ordinal agreed with the server during HELLO (CupriMark).</summary>
    public ushort NegotiatedCoreVersion { get; private set; } = 1;
    public bool IsConnected => _connection is not null;

    public event Action<MsgPayload>? MessageReceived;
    public event Action<PrivMsgPayload>? PrivateMessageReceived;

    /// <summary>A message in a room this client is in was changed by its author.</summary>
    public event Action<EditPayload>? MessageEdited;

    /// <summary>A message was taken back. Its text is gone from the server; a client showing it
    /// should stop showing the words, not merely grey them.</summary>
    public event Action<DeletePayload>? MessageDeleted;
    public event Action<JoinPayload>? MemberJoined;
    public event Action<PartPayload>? MemberParted;
    public event Action<TopicPayload>? TopicChanged;
    /// <summary>A sender began a streamed message in a room (typically an agent's token stream).</summary>
    public event Action<MsgStreamStartPayload>? MessageStreamStarted;
    public event Action<MsgStreamDeltaPayload>? MessageStreamDelta;
    /// <summary>A streamed message finished. <c>FinalText</c> is authoritative — replace the
    /// accumulated deltas with it — and <c>MessageId</c> matches the persisted history entry.</summary>
    public event Action<MsgStreamEndPayload>? MessageStreamEnded;
    /// <summary>An error that answers no outstanding request — typically a refusal of a
    /// fire-and-forget send (throttled, loop-broken, not in room). Agents watch this to learn
    /// they are being rate-limited; without it the refusal would be invisible.</summary>
    public event Action<ErrorPayload>? ServerError;
    public event Action? Disconnected;

    /// <summary>
    /// The server ended this session on purpose and said why — an admin removed the account,
    /// reset the password, changed the role, or retired the key. Fired instead of
    /// <see cref="Disconnected"/>, and the client does not redial: the credential it would
    /// redial with is the thing that just stopped existing.
    /// </summary>
    public event Action<string>? Evicted;

    /// <summary>An agent in a room asked a question. Clients render the choices on the message.</summary>
    public event Action<AskPayload>? AskReceived;

    /// <summary>A question was settled, so stop offering it — somebody else may have answered.</summary>
    public event Action<AskClosedPayload>? AskClosed;

    /// <summary>An answer to something this client asked.</summary>
    public event Action<AnswerPayload>? AnswerReceived;

    /// <summary>The reason from the server's farewell, or null while the session lives.</summary>
    public string? Farewell { get; private set; }

    /// <summary>
    /// The attributes the server actually granted this agent — pushed after every announce, and
    /// again whenever an admin changes the identity while the session runs. Compare with what was
    /// announced to see which admin overrides are in effect. Null until the first announce lands.
    /// </summary>
    public AgentAnnouncePayload? EffectiveAttributes { get; private set; }

    /// <summary>Raised whenever <see cref="EffectiveAttributes"/> changes, including live.</summary>
    public event Action<AgentAnnouncePayload>? AttributesSet;
    /// <summary>Raised before each redial attempt (1-based attempt number).</summary>
    public event Action<int>? Reconnecting;
    /// <summary>Raised after a successful redial once tracked rooms have been rejoined.</summary>
    public event Action? Reconnected;

    public static async Task<BanterClient> ConnectAsync(
        IBanterClientTransport transport,
        Uri endpoint,
        string username,
        string secret,
        BanterClientOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var client = new BanterClient(transport, endpoint, username, secret, options ?? new BanterClientOptions());
        client._connection = await client.DialAndHandshakeAsync(cancellationToken).ConfigureAwait(false);
        client.MarkReady();
        client._sessionLoop = Task.Run(client.RunSessionsAsync, CancellationToken.None);
        client._keepAliveLoop = Task.Run(client.RunKeepAliveAsync, CancellationToken.None);
        return client;
    }

    /// <summary>
    /// Connects as an enrolled agent, proving identity with the key this machine made during
    /// enrolment rather than with a password.
    ///
    /// <para><paramref name="privateKey"/> is the PKCS#8 blob <see cref="AgentEnrolment"/> returned.
    /// It is used to sign a challenge and is never sent.</para>
    /// </summary>
    public static async Task<BanterClient> ConnectWithKeyAsync(
        IBanterClientTransport transport,
        Uri endpoint,
        string username,
        byte[] privateKey,
        BanterClientOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(privateKey);

        var client = new BanterClient(
            transport, endpoint, username, secret: "", options ?? new BanterClientOptions(), privateKey);
        client._connection = await client.DialAndHandshakeAsync(cancellationToken).ConfigureAwait(false);
        client.MarkReady();
        client._sessionLoop = Task.Run(client.RunSessionsAsync, CancellationToken.None);
        client._keepAliveLoop = Task.Run(client.RunKeepAliveAsync, CancellationToken.None);
        return client;
    }

    public async Task JoinAsync(string room, CancellationToken cancellationToken = default)
    {
        await RequestAsync<OkPayload>(new JoinPayload(room), cancellationToken).ConfigureAwait(false);
        lock (_roomsLock)
        {
            _joinedRooms.Add(room);
        }
    }

    public async Task PartAsync(string room, string? reason = null, CancellationToken cancellationToken = default)
    {
        await RequestAsync<OkPayload>(new PartPayload(room, reason), cancellationToken).ConfigureAwait(false);
        lock (_roomsLock)
        {
            _joinedRooms.Remove(room);
        }
    }

    /// <summary>Fire-and-forget send; the authoritative message (id, timestamp) comes back as a
    /// <see cref="MessageReceived"/> echo to every member including this sender.</summary>
    public ValueTask SendMessageAsync(string room, string text, CancellationToken cancellationToken = default) =>
        SendAsync(_codec.CreateEnvelope(new MsgPayload(room, Nick, text, 0, null)), cancellationToken);

    /// <summary>
    /// Says something as a reply to a particular message, so an agent reading it knows which of
    /// the last ten things said it answers. Ordinary chat otherwise.
    /// </summary>
    public ValueTask ReplyAsync(
        string room, string text, string replyToMessageId, CancellationToken cancellationToken = default) =>
        SendAsync(
            _codec.CreateEnvelope(new MsgPayload(room, Nick, text, 0, null, ReplyTo: replyToMessageId)),
            cancellationToken);

    /// <summary>
    /// Asks the room a question with the ways it can be answered. Returns the ask as the server
    /// stamped it — its id is what an answer refers to.
    /// </summary>
    public Task<AskPayload> AskAsync(
        string room, IReadOnlyList<AskQuestion> questions, CancellationToken cancellationToken = default) =>
        RequestAsync<AskPayload>(new AskPayload(room, "", questions), cancellationToken);

    /// <summary>Answers somebody's question. Anyone in the room may; the first answer settles it.</summary>
    public Task<OkPayload> AnswerAsync(
        string askId, IReadOnlyList<AskAnswer> answers, CancellationToken cancellationToken = default) =>
        RequestAsync<OkPayload>(new AnswerPayload(askId, answers), cancellationToken);

    /// <summary>
    /// Changes what one of your own messages says. Only the author may edit; the server refuses
    /// anyone else with NOT_YOURS, which arrives as <see cref="ServerError"/>.
    /// </summary>
    public ValueTask EditMessageAsync(string room, string messageId, string text, CancellationToken cancellationToken = default) =>
        SendAsync(_codec.CreateEnvelope(new EditPayload(room, messageId, text)), cancellationToken);

    /// <summary>
    /// Takes a message back. The author may remove their own; an admin may remove anyone's. The
    /// text is deleted on the server rather than hidden.
    /// </summary>
    public ValueTask DeleteMessageAsync(string room, string messageId, CancellationToken cancellationToken = default) =>
        SendAsync(_codec.CreateEnvelope(new DeletePayload(room, messageId)), cancellationToken);

    /// <summary>Sends a user-to-user message. Completes on the server's Ok (delivered to at
    /// least one of the recipient's sessions); throws <see cref="BanterErrorException"/> with
    /// code NO_SUCH_USER when the recipient has no live session.</summary>
    public Task SendPrivateMessageAsync(string recipient, string text, CancellationToken cancellationToken = default) =>
        RequestAsync<OkPayload>(new PrivMsgPayload(Nick, recipient, text, 0), cancellationToken);

    public ValueTask SetTopicAsync(string room, string topic, CancellationToken cancellationToken = default) =>
        SendAsync(_codec.CreateEnvelope(new TopicPayload(room, topic)), cancellationToken);

    public Task<HistoryChunkPayload> GetHistoryAsync(
        string room, string? beforeMessageId = null, int limit = 50, CancellationToken cancellationToken = default) =>
        RequestAsync<HistoryChunkPayload>(new HistoryReqPayload(room, beforeMessageId, limit), cancellationToken);

    public Task<RoomListPayload> ListRoomsAsync(CancellationToken cancellationToken = default) =>
        RequestAsync<RoomListPayload>(new RoomListPayload([]), cancellationToken);

    public Task<RoomMembersPayload> GetMembersAsync(string room, CancellationToken cancellationToken = default) =>
        RequestAsync<RoomMembersPayload>(new RoomMembersPayload(room, []), cancellationToken);

    /// <summary>
    /// Declare what this agent is and what it may be trusted with (PLAN §8a). The server
    /// re-attributes the announcement to the authenticated nick, so <c>Nick</c> here is advisory.
    /// Announce before joining and the attributes apply on arrival.
    /// </summary>
    public Task AnnounceAgentAsync(AgentAnnouncePayload announcement, CancellationToken cancellationToken = default) =>
        RequestAsync<OkPayload>(announcement, cancellationToken);

    /// <summary>The agents in a room and their routing attributes, including who is delegator.</summary>
    public Task<AgentListPayload> GetAgentsAsync(string room, CancellationToken cancellationToken = default) =>
        RequestAsync<AgentListPayload>(new AgentListPayload(room, []), cancellationToken);

    /// <summary>Read or change a room's dispatch mode. Returns the mode in effect afterwards.</summary>
    public Task<RoomModePayload> SetRoomModeAsync(
        string room, RoomDispatchMode mode, CancellationToken cancellationToken = default) =>
        RequestAsync<RoomModePayload>(new RoomModePayload(room, mode), cancellationToken);

    /// <summary>
    /// Open a room. With <paramref name="parentRoom"/> set it is a sub-room, which inherits the
    /// parent's sensitivity — a child room is never more permissive than the conversation that
    /// spawned it. The caller joins it automatically.
    /// </summary>
    /// <summary>Open a child room of one you are in; it inherits the parent's sensitivity.</summary>
    public Task<RoomCreatePayload> CreateSubRoomAsync(
        string room, string parentRoom, string purpose = "", CancellationToken cancellationToken = default) =>
        CreateRoomAsync(room, parentRoom, purpose, cancellationToken);

    public Task<RoomCreatePayload> CreateRoomAsync(
        string room, string? parentRoom = null, string purpose = "", CancellationToken cancellationToken = default) =>
        RequestAsync<RoomCreatePayload>(new RoomCreatePayload(room, parentRoom, purpose), cancellationToken);

    /// <summary>
    /// Pull an agent into a room you are the delegator of. Refused when the agent is not cleared
    /// for that room's sensitivity.
    /// </summary>
    public Task MoveAgentAsync(
        string nick, string room, string reason = "", CancellationToken cancellationToken = default) =>
        RequestAsync<OkPayload>(new AgentMovePayload(nick, room, reason), cancellationToken);

    // ── Work ledger (PLAN §8b) ───────────────────────────────────────────────────────────────

    /// <summary>Post work into a room. The reply carries the server-assigned task id.</summary>
    public Task<TaskInfoPayload> PostTaskAsync(
        string room, string title, string body = "", int leaseSeconds = 0,
        CancellationToken cancellationToken = default) =>
        RequestAsync<TaskInfoPayload>(new TaskPostPayload(room, title, body, leaseSeconds), cancellationToken);

    /// <summary>Claim an open task. Throws <c>TASK_TAKEN</c> if another agent got there first.</summary>
    public Task<TaskInfoPayload> ClaimTaskAsync(string taskId, CancellationToken cancellationToken = default) =>
        RequestAsync<TaskInfoPayload>(new TaskClaimPayload(taskId), cancellationToken);

    /// <summary>Assign a task to an agent. Delegator-only.</summary>
    public Task<TaskInfoPayload> AssignTaskAsync(
        string taskId, string nick, CancellationToken cancellationToken = default) =>
        RequestAsync<TaskInfoPayload>(new TaskAssignPayload(taskId, nick), cancellationToken);

    /// <summary>Post progress, which also renews the lease on a task you hold.</summary>
    public Task UpdateTaskAsync(string taskId, string note, CancellationToken cancellationToken = default) =>
        RequestAsync<OkPayload>(new TaskUpdatePayload(taskId, note), cancellationToken);

    /// <summary>Give a task back to the pool.</summary>
    public Task ReleaseTaskAsync(
        string taskId, string reason = "", CancellationToken cancellationToken = default) =>
        RequestAsync<OkPayload>(new TaskReleasePayload(taskId, reason), cancellationToken);

    /// <summary>Finish a task you hold.</summary>
    public Task CompleteTaskAsync(
        string taskId, string result = "", bool success = true, CancellationToken cancellationToken = default) =>
        RequestAsync<OkPayload>(new TaskDonePayload(taskId, result, success), cancellationToken);

    /// <summary>Tasks in a room; terminal ones excluded unless asked for.</summary>
    /// <summary>
    /// Every room's work, not just one. Admin-only: it is the operator's view of what the server
    /// is doing, which no single room's board can show.
    /// </summary>
    public Task<TaskListPayload> ListAllTasksAsync(
        bool includeFinished = false, CancellationToken cancellationToken = default) =>
        RequestAsync<TaskListPayload>(new TaskListPayload("", [], includeFinished), cancellationToken);

    public Task<TaskListPayload> ListTasksAsync(
        string room, bool includeFinished = false, CancellationToken cancellationToken = default) =>
        RequestAsync<TaskListPayload>(new TaskListPayload(room, [], includeFinished), cancellationToken);

    /// <summary>
    /// The tools available to you. An agent gets the set it was granted; an admin gets the whole
    /// connected catalogue, which is what the management panel grants from.
    /// </summary>
    public Task<ToolListPayload> ListToolsAsync(CancellationToken cancellationToken = default) =>
        RequestAsync<ToolListPayload>(new ToolListPayload([]), cancellationToken);

    /// <summary>
    /// The tool hubs this server defers to: whether it can reach each one, how much each offers,
    /// and which agents hold an identity there.
    ///
    /// <para>Admin-only, because it is a map of the estate rather than a list of what you may
    /// call. It carries no keys — an agent's hub key stays with the server that calls as it, and
    /// is never put on the wire (see <c>HubAgentPayload</c>).</para>
    /// </summary>
    public async Task<IReadOnlyList<HubPayload>> InspectHubsAsync(CancellationToken cancellationToken = default) =>
        (await RequestAsync<HubReportPayload>(new HubInspectPayload(), cancellationToken)
            .ConfigureAwait(false)).Hubs;

    /// <summary>
    /// Ask the server to run a tool. The server executes it — this client never holds the
    /// upstream's credentials (PLAN §8). Name the room and the call is announced there, so the
    /// operator watching can see what the agent reached for.
    /// </summary>
    public Task<ToolResultPayload> CallToolAsync(
        string name, string arguments = "", string room = "", CancellationToken cancellationToken = default) =>
        RequestAsync<ToolResultPayload>(new ToolCallPayload(name, arguments, room), cancellationToken);

    /// <summary>Read an agent's tool grants. Admin only.</summary>
    public Task<ToolGrantsPayload> GetToolGrantsAsync(
        string agent, CancellationToken cancellationToken = default) =>
        RequestAsync<ToolGrantsPayload>(new ToolGrantsPayload(agent, [], Replace: false), cancellationToken);

    /// <summary>
    /// Replace an agent's tool grants. Admin only, and wholesale: the list you send becomes the
    /// list it holds, so an empty list revokes everything.
    /// </summary>
    public Task<ToolGrantsPayload> SetToolGrantsAsync(
        string agent, IReadOnlyList<string> tools, CancellationToken cancellationToken = default) =>
        RequestAsync<ToolGrantsPayload>(new ToolGrantsPayload(agent, tools, Replace: true), cancellationToken);

    /// <summary>Raised on every task state change in a room you are in.</summary>
    public event Action<TaskInfoPayload>? TaskChanged;

    /// <summary>Raised when a room's delegator changes, including on join.</summary>
    public event Action<RoomDelegatorPayload>? DelegatorChanged;

    /// <summary>Raised when a room's dispatch mode changes.</summary>
    public event Action<RoomModePayload>? RoomModeChanged;

    /// <summary>
    /// Opens a streamed message in a room: deltas render live in every member's client and the
    /// completed text lands in history as one message. This is the path agent token streams take
    /// (PLAN §4). Dispose without completing and the server still closes the stream from the
    /// deltas it received.
    /// </summary>
    public async Task<BanterMessageStream> StartMessageStreamAsync(string room, CancellationToken cancellationToken = default)
    {
        var streamId = Guid.NewGuid().ToString("N");
        await RequestAsync<OkPayload>(new MsgStreamStartPayload(room, Nick, streamId), cancellationToken).ConfigureAwait(false);
        return new BanterMessageStream(this, streamId);
    }

    internal ValueTask SendStreamDeltaAsync(string streamId, string delta, CancellationToken cancellationToken) =>
        SendAsync(_codec.CreateEnvelope(new MsgStreamDeltaPayload(streamId, delta)), cancellationToken);

    internal ValueTask SendStreamEndAsync(string streamId, string finalText, CancellationToken cancellationToken) =>
        SendAsync(_codec.CreateEnvelope(new MsgStreamEndPayload(streamId, finalText, 0)), cancellationToken);

    // ---- Files (room-scoped storage) ----

    private const int UploadChunkBytes = 64 * 1024;

    /// <summary>
    /// Uploads content to a room. Deduplicated server-side by hash — a second upload of identical
    /// bytes completes without sending chunks. Unless <paramref name="quiet"/>, the server announces
    /// the file in the room as a message carrying the file reference.
    ///
    /// <para><b>Sent with a manifest</b> — a hash per chunk — so the server verifies each chunk as it
    /// lands and can say which it is still missing. That turns the thing an upload is most likely to
    /// meet, a connection going mid-transfer, from starting again into sending the remainder. A server
    /// that predates the manifest ignores it and appends as it always did, and this still works;
    /// see <see cref="FilePutStartPayload"/>.</para>
    /// </summary>
    public async Task<FileInfoPayload> UploadFileAsync(
        string room,
        string name,
        ReadOnlyMemory<byte> content,
        string mimeType,
        string? description = null,
        bool quiet = false,
        CancellationToken cancellationToken = default)
    {
        var sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(content.Span));
        var chunkHashes = DescribeChunks(content);

        // A manifest is 32 bytes per chunk, and FILE_PUT_START has to fit one frame like anything
        // else. At 64 KiB chunks over a conduit's 192 KiB ceiling that is a few hundred megabytes of
        // file before it matters, and the default file cap is 32 MB — but a deployment that raised
        // the cap would otherwise find large files became unuploadable rather than merely unverified.
        // So an oversized manifest is dropped instead, and the upload is the append-only one it was
        // before manifests existed.
        var manifested = (chunkHashes.Length * 40) + 1024
                         <= (_connection?.MaxFrameBytes ?? BanterFraming.DefaultMaxFrameBytes);

        // The id of an upload the server has already opened, once there is one. Holding it is what
        // makes a retry a resume: without it the far side has an orphan and we have a whole file to
        // send again.
        string? inFlight = null;

        // Three attempts rather than the one this used to allow, and the bound now means something
        // different. It used to cap WASTED WORK: a second attempt resent the entire file, so a third
        // was rarely worth waiting for. Every attempt now sends only what is still owed, so the bound
        // is only about a connection that is genuinely gone rather than one being replaced — and the
        // caller should hear about that instead of watching us retry.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (inFlight is null)
                {
                    var start = await RequestAsync<FileInfoPayload>(
                        new FilePutStartPayload(
                            room, name, mimeType, content.Length, sha, description, quiet,
                            manifested ? UploadChunkBytes : 0,
                            manifested ? chunkHashes : null),
                        cancellationToken).ConfigureAwait(false);
                    if (start.Complete)
                    {
                        return start;
                    }

                    inFlight = start.FileId;
                    await SendChunksAsync(inFlight, content, Enumerable.Range(0, ChunkCount(content.Length)), cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    // What the server still wants, which after a drop is not the same as what we
                    // never sent: chunks in flight when the connection went may well have landed.
                    var owed = await RequestAsync<FilePutResumePayload>(
                        FilePutResumePayload.Request(inFlight), cancellationToken).ConfigureAwait(false);
                    await SendChunksAsync(inFlight, content, owed.Missing, cancellationToken).ConfigureAwait(false);
                }


                return await RequestAsync<FileInfoPayload>(new FilePutEndPayload(inFlight), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (BanterDisconnectedException) when (attempt < 3)
            {
                // Round again. With an id in hand and a manifest behind it this resumes; otherwise
                // there is nothing on the far side worth asking about, so drop the id and start —
                // asking would spend a round trip to be told NOT_RESUMABLE.
                if (!manifested)
                {
                    inFlight = null;
                }
            }
            catch (BanterErrorException error) when (attempt < 3 && inFlight is not null && CanOnlyStartOver(error.Code))
            {
                // The upload we were holding is not there to continue — a restarted server, or one
                // that never kept chunk state because it ignored the manifest. Begin a new one.
                inFlight = null;
            }
        }
    }

    /// <summary>
    /// Codes that mean "that upload is gone, open another" rather than "this upload failed".
    ///
    /// <para><c>UNSUPPORTED</c> is a server with no <c>FILE_PUT_RESUME</c> at all, and
    /// <c>NOT_RESUMABLE</c> one that took the append-only path because it did not understand the
    /// manifest. Both are older servers, and on both the answer is the behaviour this method had
    /// before manifests existed: send it again from the beginning.</para>
    /// </summary>
    private static bool CanOnlyStartOver(string code) =>
        code is "UPLOAD_NOT_FOUND" or "NOT_RESUMABLE" or "UNSUPPORTED";

    private static int ChunkCount(int length) => (length + UploadChunkBytes - 1) / UploadChunkBytes;

    /// <summary>
    /// The SHA-256 of each chunk, in order — the manifest the server verifies against.
    ///
    /// <para>Hashed with the BCL rather than through CupriNet's Reliquary builder, which wants the
    /// whole file in one span and a crypto suite: this assembly is transport-free and runs in a
    /// browser, and the hash is the same hash either way.</para>
    /// </summary>
    private static byte[][] DescribeChunks(ReadOnlyMemory<byte> content)
    {
        var hashes = new byte[ChunkCount(content.Length)][];
        for (var index = 0; index < hashes.Length; index++)
        {
            var offset = index * UploadChunkBytes;
            var slice = content.Span[offset..Math.Min(offset + UploadChunkBytes, content.Length)];
            hashes[index] = System.Security.Cryptography.SHA256.HashData(slice);
        }

        return hashes;
    }

    /// <summary>Sends the named chunks, each at the offset that identifies it.</summary>
    private async Task SendChunksAsync(
        string fileId,
        ReadOnlyMemory<byte> content,
        IEnumerable<int> indices,
        CancellationToken cancellationToken)
    {
        foreach (var index in indices)
        {
            var offset = index * UploadChunkBytes;
            var slice = content[offset..Math.Min(offset + UploadChunkBytes, content.Length)];
            await RequestAsync<OkPayload>(
                new FilePutChunkPayload(fileId, offset, slice.ToArray()), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Fetches a stored file whole, verified against the server's own description of it.
    ///
    /// <para>Three paths, and all three end with bytes that were checked. A <b>relic</b> where the
    /// transport has one — chunk by chunk against a manifest, on its own logical stream, so a large
    /// download does not sit in front of the room's chat. Otherwise a <b>manifested</b> FILE_GET loop,
    /// which verifies the same way over the frame pipe and resumes from what it already holds if the
    /// connection goes. And where the server cannot describe the file, the <b>plain</b> loop this
    /// method has always had: bytes in order, trusted, begun again from nothing.</para>
    ///
    /// <para>The plain path is the only one that was ever here, and it verified nothing at all — a
    /// truncated or corrupted download was indistinguishable from a short file. It survives because
    /// some server, somewhere, is older than the manifest.</para>
    /// </summary>
    public async Task<byte[]> DownloadFileAsync(string fileId, CancellationToken cancellationToken = default)
    {
        if (RelicFetch is { } relics)
        {
            var fetched = await TryFetchAsRelicAsync(relics, fileId, cancellationToken).ConfigureAwait(false);
            if (fetched is not null)
            {
                return fetched;
            }
        }

        FileManifestPayload? manifest;
        try
        {
            manifest = await RequestAsync<FileManifestPayload>(
                FileManifestPayload.Request(fileId), cancellationToken).ConfigureAwait(false);
        }
        catch (BanterErrorException error) when (error.Code is "NO_MANIFEST" or "UNSUPPORTED")
        {
            // A file too big to describe inside this connection's frames, or a server that predates
            // FILE_MANIFEST. Neither is a fault, and the bytes are still reachable.
            manifest = null;
        }

        return manifest is null
            ? await GetUnverifiedAsync(fileId, cancellationToken).ConfigureAwait(false)
            : await GetVerifiedAsync(manifest, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Fetches every chunk the manifest names, checking each against its hash, and resuming rather
    /// than restarting when the connection goes.
    ///
    /// <para>Held as one buffer of the declared size and filled at each chunk's own offset, so a
    /// resume needs no bookkeeping beyond which chunks have landed — and a chunk that arrives twice
    /// overwrites itself with the same bytes.</para>
    ///
    /// <para>A chunk that fails its hash is <b>fatal</b>, and deliberately not retried — which is the
    /// opposite of the upload side, for a reason. There, a bad chunk is bytes that went wrong in
    /// flight and the sender still holds the right ones. Here the server is serving what it has, so a
    /// mismatch says its stored copy no longer matches the manifest it published for it: asking again
    /// gets the same wrong bytes. Returning them would be worse than failing, and looping would hide
    /// it, so it surfaces as an exception naming the chunk.</para>
    /// </summary>
    private async Task<byte[]> GetVerifiedAsync(FileManifestPayload manifest, CancellationToken cancellationToken)
    {
        var content = new byte[manifest.Size];
        var have = new bool[manifest.ChunkHashes.Count];

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                for (var index = 0; index < have.Length; index++)
                {
                    if (have[index])
                    {
                        continue;
                    }

                    var offset = (long)index * manifest.ChunkBytes;
                    var expected = (int)Math.Min(manifest.ChunkBytes, manifest.Size - offset);
                    var chunk = await RequestAsync<FileChunkPayload>(
                        new FileGetPayload(manifest.FileId, offset, expected), cancellationToken).ConfigureAwait(false);

                    if (chunk.Data.Length != expected
                        || !System.Security.Cryptography.SHA256.HashData(chunk.Data)
                            .SequenceEqual(manifest.ChunkHashes[index]))
                    {
                        throw new BanterClientException(
                            $"Chunk {index} of '{manifest.FileId}' does not match the manifest the server gave for it.");
                    }

                    chunk.Data.CopyTo(content, (int)offset);
                    have[index] = true;
                }

                return content;
            }
            catch (BanterDisconnectedException) when (attempt < 3)
            {
                // Round again, keeping every chunk that already verified.
            }
        }
    }

    /// <summary>The loop this method had before manifests: offsets in order, nothing checked.</summary>
    private async Task<byte[]> GetUnverifiedAsync(string fileId, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        long offset = 0;
        while (true)
        {
            var chunk = await RequestAsync<FileChunkPayload>(
                new FileGetPayload(fileId, offset, UploadChunkBytes), cancellationToken).ConfigureAwait(false);
            buffer.Write(chunk.Data);
            offset += chunk.Data.Length;
            if (chunk.Eof)
            {
                return buffer.ToArray();
            }
        }
    }

    /// <summary>
    /// The transport's bulk-fetch rite, or null when it has none (PLAN §2.5).
    ///
    /// <para>Exposed because <see cref="DownloadFileAsync"/> is not the only sane thing to do with a
    /// relic: it returns a whole file as one array, which is the wrong shape for something being
    /// written to disk. A caller that wants to drive the transfer itself pairs this with
    /// <see cref="RequestRelicAsync"/>. Null is the ordinary answer on TCP and WebSocket, and means
    /// "use the file verbs", not "no files".</para>
    /// </summary>
    public IBanterRelicFetch? RelicFetch => _connection as IBanterRelicFetch;

    /// <summary>
    /// Asks the server to name this file as a relic, having checked that this session may read it.
    ///
    /// <para>The reply's name is a short-lived bearer capability — see <c>FileRelicPayload</c> for
    /// what that does and does not mean. Servers with no relic rite answer <c>NO_RELICS</c>.</para>
    /// </summary>
    public Task<FileRelicPayload> RequestRelicAsync(string fileId, CancellationToken cancellationToken = default) =>
        RequestAsync<FileRelicPayload>(FileRelicPayload.Request(fileId), cancellationToken);

    /// <summary>
    /// Asks for a relic ticket and fetches it, or returns null for the caller to fall back.
    ///
    /// <para>Null means "this file did not come over the relic path", never "this file is
    /// unavailable". Three codes say so and all three ask for the frame path rather than reporting a
    /// fault: <c>NO_RELICS</c> from a server that serves none, <c>RELIC_BUSY</c> from one out of
    /// ticket space, and <c>UNSUPPORTED</c> from one predating <c>FILE_RELIC</c> altogether — a
    /// server answers that for any message type it has no contract for, which is the mechanism that
    /// keeps mixed-version fleets talking, and forgetting it here would turn "your server is older
    /// than your client" into every download failing.</para>
    ///
    /// <para>An access refusal is NOT swallowed: <c>NO_ACCESS</c> means the download would fail on
    /// either path, and retrying it frame by frame only makes the same error arrive later.</para>
    ///
    /// <para>A ticket has a lifetime, so it is fetched at once rather than held. A transfer that
    /// outlives its ticket fails on a chunk, which the caller can only answer by asking again — and
    /// the fallback below is a better answer than a retry loop while the reason is unknown.</para>
    /// </summary>
    private async Task<byte[]?> TryFetchAsRelicAsync(
        IBanterRelicFetch relics,
        string fileId,
        CancellationToken cancellationToken)
    {
        FileRelicPayload ticket;
        try
        {
            ticket = await RequestRelicAsync(fileId, cancellationToken).ConfigureAwait(false);
        }
        catch (BanterErrorException error) when (error.Code is "NO_RELICS" or "RELIC_BUSY" or "UNSUPPORTED")
        {
            return null;
        }

        try
        {
            // The server's stated length is the buffer bound. The fetch refuses a manifest claiming
            // more, so the two have to agree for anything to be allocated.
            return await relics.FetchRelicAsync(ticket.RelicName, ticket.Length, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A relic-side failure: an expired name, a refused chunk, a hash that did not match, or
            // a node whose store moved under a live ticket. The bytes are still reachable the other
            // way, and one round trip is a cheaper answer than surfacing a transport's rite to a
            // caller that asked for a file.
            RelicFetchFailed?.Invoke(fileId, exception);
            return null;
        }
    }

    /// <summary>
    /// A relic fetch fell back to the frame path, and why. Nothing breaks when it fires — the
    /// download still completes — which is exactly why it is worth surfacing: a mesh deployment
    /// silently taking the slow path on every file is invisible otherwise.
    /// </summary>
    public event Action<string, Exception>? RelicFetchFailed;

    public Task<FileListPayload> ListFilesAsync(string room, CancellationToken cancellationToken = default) =>
        RequestAsync<FileListPayload>(new FileListPayload(room, []), cancellationToken);

    public Task<FileInfoPayload> GetFileInfoAsync(string fileId, CancellationToken cancellationToken = default) =>
        RequestAsync<FileInfoPayload>(FileInfoPayload.Request(fileId), cancellationToken);

    public Task GrantFileAsync(string fileId, string room, CancellationToken cancellationToken = default) =>
        RequestAsync<OkPayload>(new FileGrantPayload(fileId, room), cancellationToken);

    public Task RevokeFileAsync(string fileId, string room, CancellationToken cancellationToken = default) =>
        RequestAsync<OkPayload>(new FileRevokePayload(fileId, room), cancellationToken);

    public Task DeleteFileAsync(string fileId, CancellationToken cancellationToken = default) =>
        RequestAsync<OkPayload>(new FileDeletePayload(fileId), cancellationToken);

    public async Task<TimeSpan> PingAsync(CancellationToken cancellationToken = default)
    {
        var sent = DateTimeOffset.UtcNow;

        // waitForReady: false - a ping measures the connection there is, so holding one until a
        // redial finishes would report the wait rather than the round trip, and the keep-alive
        // would sit on the gate instead of ticking.
        await RequestAsync<PongPayload>(
            new PingPayload(sent.ToUnixTimeMilliseconds()), cancellationToken, waitForReady: false)
            .ConfigureAwait(false);
        return DateTimeOffset.UtcNow - sent;
    }

    /// <summary>
    /// Pings while the connection is otherwise quiet, so the session stays alive and a dead one is
    /// noticed promptly rather than at the moment someone tries to speak.
    ///
    /// <para>Failures are swallowed on purpose. A ping that does not come back means the
    /// connection has gone, which the session loop is already watching for and already knows how
    /// to redial — reporting it here as well would surface one drop twice, and tearing the client
    /// down over it would defeat reconnect entirely.</para>
    /// </summary>
    private async Task RunKeepAliveAsync()
    {
        if (_options.KeepAliveInterval <= TimeSpan.Zero)
        {
            return;
        }

        using var ticks = new PeriodicTimer(_options.KeepAliveInterval);

        try
        {
            while (await ticks.WaitForNextTickAsync(_lifecycle.Token).ConfigureAwait(false))
            {
                // Nothing to keep alive between a drop and the redial, and a request sent then
                // would only wait out its own timeout.
                if (_connection is null)
                {
                    continue;
                }

                try
                {
                    await PingAsync(_lifecycle.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_lifecycle.IsCancellationRequested)
                {
                    return;
                }
                catch
                {
                    // The session loop owns what a dead connection means.
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Disposed.
        }
    }

    // ---- Connection lifecycle ----

    /// <summary>Dials and completes HELLO + AUTH over the raw connection (no receive loop yet),
    /// so the same path serves both the first connection and every reconnect.</summary>
    private async Task<IBanterConnection> DialAndHandshakeAsync(CancellationToken cancellationToken)
    {
        var connection = await _transport.ConnectAsync(_endpoint, cancellationToken).ConfigureAwait(false);
        try
        {
            var version = typeof(BanterClient).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
            var helloReply = await RawRequestAsync(
                connection,
                new HelloPayload(_options.ClientName, version, ["banter.core"], BanterCatalog.LocalRanges()),
                cancellationToken).ConfigureAwait(false);
            switch (helloReply)
            {
                case HelloPayload serverHello when BanterCatalog.TryNegotiateCore(serverHello.Ranges, out var negotiated):
                    NegotiatedCoreVersion = negotiated;
                    break;
                case HelloPayload:
                    throw new BanterClientException(
                        $"No mutually supported {BanterCatalog.CoreComponent} revision (this client speaks {BanterCatalog.SupportedCore}).");
                case ErrorPayload error:
                    throw new BanterClientException($"Server refused HELLO: {error.Code}: {error.Message}");
                default:
                    throw new BanterClientException($"Unexpected HELLO reply: {helloReply?.GetType().Name ?? "null"}.");
            }

            var reply = _privateKey is null
                ? await RawRequestAsync(connection, new AuthPayload(_username, _secret, IsAgentToken: false), cancellationToken)
                    .ConfigureAwait(false)
                : await AuthenticateWithKeyAsync(connection, cancellationToken).ConfigureAwait(false);
            switch (reply)
            {
                case AuthOkPayload ok:
                    Nick = ok.Nick;
                    IsAgent = ok.IsAgent;
                    IsAdmin = ok.IsAdmin;
                    SessionId = ok.SessionId;
                    return connection;
                case AuthFailPayload fail:
                    throw new BanterAuthException(fail.Reason);
                default:
                    throw new BanterClientException($"Unexpected AUTH reply: {reply?.GetType().Name ?? "null"}.");
            }
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Proves this machine holds the agent's key, instead of sending a password.
    ///
    /// <para>Two round trips rather than one: the server picks the nonce, so a signature captured
    /// off the wire is spent and cannot be replayed. The private key is used here and never
    /// leaves — what crosses the wire is a signature over a number the server chose.</para>
    /// </summary>
    private async Task<object?> AuthenticateWithKeyAsync(IBanterConnection connection, CancellationToken cancellationToken)
    {
        var issued = await RawRequestAsync(connection, new AuthChallengePayload(_username), cancellationToken)
            .ConfigureAwait(false);

        if (issued is not AuthChallengeIssuedPayload challenge)
        {
            return issued;                              // an error or a surprise; the caller reports it
        }

        var signature = AgentKeys.Sign(
            _privateKey!, AgentKeys.ChallengeBytes(_username, challenge.Nonce));

        return await RawRequestAsync(connection, new AuthKeyPayload(_username, signature), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Request/response over a raw connection, skipping pushes. Used only during the
    /// handshake, before the receive loop owns the connection.</summary>
    private async Task<object?> RawRequestAsync(IBanterConnection connection, object payload, CancellationToken cancellationToken)
    {
        var envelope = _codec.CreateEnvelope(payload);
        await connection.SendFrameAsync(_codec.EncodeEnvelope(envelope), cancellationToken).ConfigureAwait(false);
        var deadline = DateTimeOffset.UtcNow + _options.RequestTimeout;
        while (true)
        {
            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                throw new TimeoutException($"No reply to {payload.GetType().Name} within {_options.RequestTimeout}.");
            }

            var frame = await connection.ReceiveFrameAsync(cancellationToken).AsTask().WaitAsync(remaining, cancellationToken)
                .ConfigureAwait(false);
            if (frame is null)
            {
                throw new BanterDisconnectedException();
            }

            var received = _codec.DecodeEnvelope(frame);
            if (received.ReplyTo == envelope.MsgId)
            {
                return _codec.DecodePayload(received);
            }
        }
    }

    private async Task RunSessionsAsync()
    {
        var cancellationToken = _lifecycle.Token;
        while (true)
        {
            await ReceiveUntilClosedAsync(_connection!, cancellationToken).ConfigureAwait(false);
            _connection = null;
            MarkNotReady();
            FailPending();
            if (Farewell is { } farewell)
            {
                // A deliberate goodbye, not a dropped wire. No redial: the credential this
                // client holds is the thing the server just retired.
                AbandonWaiters();
                Evicted?.Invoke(farewell);
                return;
            }

            if (_disposed || cancellationToken.IsCancellationRequested)
            {
                AbandonWaiters();
                return;
            }

            Disconnected?.Invoke();
            if (!_options.AutoReconnect)
            {
                AbandonWaiters();
                return;
            }

            var next = await RedialWithBackoffAsync(cancellationToken).ConfigureAwait(false);
            if (next is null)
            {
                // Out of attempts, or the credential stopped working. Nothing is coming.
                AbandonWaiters();
                return;
            }

            _connection = next;
            _ = Task.Run(() => RejoinAsync(cancellationToken), CancellationToken.None);
        }
    }

    private async Task<IBanterConnection?> RedialWithBackoffAsync(CancellationToken cancellationToken)
    {
        var attempt = 0;
        var delay = _options.ReconnectInitialDelay;
        while (!cancellationToken.IsCancellationRequested)
        {
            attempt++;
            Reconnecting?.Invoke(attempt);
            try
            {
                // Jittered to 50-100% of the nominal delay. One server restart drops every
                // connected client at the same instant; without jitter they all redial in
                // lockstep on the same schedule, and a fleet of agents becomes a synchronised
                // thundering herd against a server that is still starting up.
                var wait = TimeSpan.FromTicks((long)(delay.Ticks * (0.5 + Random.Shared.NextDouble() * 0.5)));
                await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
                return await DialAndHandshakeAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (BanterAuthException)
            {
                // Credentials no longer valid — retrying cannot help.
                return null;
            }
            catch
            {
                delay = delay >= _options.ReconnectMaxDelay
                    ? _options.ReconnectMaxDelay
                    : TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, _options.ReconnectMaxDelay.Ticks));
            }
        }

        return null;
    }

    private async Task RejoinAsync(CancellationToken cancellationToken)
    {
        string[] rooms;
        lock (_roomsLock)
        {
            rooms = [.. _joinedRooms];
        }

        foreach (var room in rooms)
        {
            try
            {
                // waitForReady: false - this IS what the gate is waiting for. Waiting on itself
                // would deadlock until the grace ran out and then rejoin nothing.
                await RequestAsync<OkPayload>(new JoinPayload(room), cancellationToken, waitForReady: false)
                    .ConfigureAwait(false);
            }
            catch
            {
                // A failed rejoin (room deleted, connection dropped again) shouldn't kill the
                // others; the next disconnect cycle retries.
            }
        }

        // Only now is the session what it was before the drop, so only now do held sends go.
        MarkReady();
        Reconnected?.Invoke();
    }

    private async Task ReceiveUntilClosedAsync(IBanterConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                var frame = await connection.ReceiveFrameAsync(cancellationToken).ConfigureAwait(false);
                if (frame is null)
                {
                    return;
                }

                var envelope = _codec.DecodeEnvelope(frame);
                var payload = _codec.DecodePayload(envelope);

                if (envelope.ReplyTo is not null && _pending.TryRemove(envelope.ReplyTo, out var tcs))
                {
                    tcs.TrySetResult(payload);
                    continue;
                }

                switch (payload)
                {
                    case AgentAnnouncePayload effective:
                        EffectiveAttributes = effective;
                        AttributesSet?.Invoke(effective);
                        break;

                    case AskPayload ask:
                        AskReceived?.Invoke(ask);
                        break;

                    case AskClosedPayload closed:
                        AskClosed?.Invoke(closed);
                        break;

                    case AnswerPayload answered:
                        AnswerReceived?.Invoke(answered);
                        break;

                    case ByePayload bye:
                        Farewell = bye.Reason ?? "The server ended this session.";
                        return;

                    case MsgPayload msg:
                        MessageReceived?.Invoke(msg);
                        break;
                    case PrivMsgPayload priv:
                        PrivateMessageReceived?.Invoke(priv);
                        break;
                    case EditPayload edit:
                        MessageEdited?.Invoke(edit);
                        break;
                    case DeletePayload delete:
                        MessageDeleted?.Invoke(delete);
                        break;
                    case JoinPayload join:
                        MemberJoined?.Invoke(join);
                        break;
                    case PartPayload part:
                        MemberParted?.Invoke(part);
                        break;
                    case TopicPayload topic:
                        TopicChanged?.Invoke(topic);
                        break;
                    case RoomDelegatorPayload delegator:
                        DelegatorChanged?.Invoke(delegator);
                        break;
                    case RoomModePayload roomMode:
                        RoomModeChanged?.Invoke(roomMode);
                        break;
                    case TaskInfoPayload task:
                        TaskChanged?.Invoke(task);
                        break;
                    case MsgStreamStartPayload streamStart:
                        MessageStreamStarted?.Invoke(streamStart);
                        break;
                    case MsgStreamDeltaPayload streamDelta:
                        MessageStreamDelta?.Invoke(streamDelta);
                        break;
                    case MsgStreamEndPayload streamEnd:
                        MessageStreamEnded?.Invoke(streamEnd);
                        break;
                    case ErrorPayload serverError:
                        ServerError?.Invoke(serverError);
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or InvalidDataException
            or ObjectDisposedException or OperationCanceledException or MessagePack.MessagePackSerializationException)
        {
            // Treated as a disconnect; the session loop decides what happens next.
        }
        finally
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    // ---- Requests ----

    private async Task<TReply> RequestAsync<TReply>(
        object payload, CancellationToken cancellationToken, bool waitForReady = true)
        where TReply : class
    {
        var envelope = _codec.CreateEnvelope(payload);
        var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[envelope.MsgId] = tcs;
        try
        {
            await SendAsync(envelope, cancellationToken, waitForReady).ConfigureAwait(false);
            var reply = await tcs.Task.WaitAsync(_options.RequestTimeout, cancellationToken).ConfigureAwait(false);
            return reply switch
            {
                TReply typed => typed,
                ErrorPayload error => throw new BanterErrorException(error),
                _ => throw new BanterClientException(
                    $"Expected {typeof(TReply).Name} in reply to {payload.GetType().Name}, got {reply?.GetType().Name ?? "null"}."),
            };
        }
        finally
        {
            _pending.TryRemove(envelope.MsgId, out _);
        }
    }

    private async ValueTask SendAsync(
        BanterEnvelope envelope, CancellationToken cancellationToken, bool waitForReady = true)
    {
        var connection = waitForReady
            ? await AwaitReadyAsync(cancellationToken).ConfigureAwait(false)
            : _connection ?? throw new BanterDisconnectedException();

        var frame = _codec.EncodeEnvelope(envelope);
        try
        {
            await connection.SendFrameAsync(frame, cancellationToken).ConfigureAwait(false);
            return;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            if (!waitForReady)
            {
                throw new BanterDisconnectedException();
            }

            // A connection can die between being handed over and being written to — and on the
            // drop this exists for, it is the write that finds out first, before the receive loop
            // has noticed anything. Shutting the gate here rather than waiting to be told is what
            // stops the retry below being handed the same dead socket.
            Invalidate(connection);
        }

        // Once, on whatever the redial produced. A frame that failed to write never reached the
        // server to be duplicated: the length prefix means a partial write is a truncated frame,
        // which ends that session without being acted on.
        var replacement = await AwaitReadyAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await replacement.SendFrameAsync(frame, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            throw new BanterDisconnectedException();
        }
    }

    /// <summary>
    /// Marks a connection as the dead one, if it is still the current one. The session loop is
    /// the owner of what comes next; this only stops sends being released at a socket that has
    /// already failed.
    /// </summary>
    private void Invalidate(IBanterConnection connection)
    {
        if (ReferenceEquals(_connection, connection))
        {
            MarkNotReady();
        }
    }

    /// <summary>
    /// The live connection, waiting out a redial in progress rather than failing into it.
    ///
    /// <para>Only worth waiting when a redial is actually coming: a client with reconnect turned
    /// off, one already disposed, and one the server said goodbye to are all staying down, and
    /// making the caller wait for that would be a slower way of saying the same thing.</para>
    /// </summary>
    private async ValueTask<IBanterConnection> AwaitReadyAsync(CancellationToken cancellationToken)
    {
        var ready = Volatile.Read(ref _ready);
        if (ready.Task.IsCompleted && _connection is { } live)
        {
            return live;
        }

        if (!_options.AutoReconnect || _options.ReconnectGrace <= TimeSpan.Zero
            || _disposed || Farewell is not null || _lifecycle.IsCancellationRequested)
        {
            throw new BanterDisconnectedException();
        }

        try
        {
            await ready.Task.WaitAsync(_options.ReconnectGrace, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new BanterDisconnectedException();
        }

        // The redial that completed this source could itself have dropped in the meantime; one
        // wait is the promise, not a loop that never gives an answer.
        return _connection ?? throw new BanterDisconnectedException();
    }

    /// <summary>Opens the gate anything waiting to send is holding at.</summary>
    private void MarkReady() => Volatile.Read(ref _ready).TrySetResult();

    /// <summary>
    /// Shuts the gate, so the next send waits for the redial instead of being thrown away at a
    /// connection that is already gone. Only a source that has already been opened is replaced,
    /// which is what makes the one anything is waiting on the one that gets completed.
    /// </summary>
    private void MarkNotReady()
    {
        var current = Volatile.Read(ref _ready);
        if (current.Task.IsCompleted)
        {
            Interlocked.CompareExchange(
                ref _ready, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously), current);
        }
    }

    /// <summary>Releases everything waiting for a redial that is never coming.</summary>
    private void AbandonWaiters() =>
        Volatile.Read(ref _ready).TrySetException(new BanterDisconnectedException());

    private void FailPending()
    {
        foreach (var key in _pending.Keys)
        {
            if (_pending.TryRemove(key, out var tcs))
            {
                tcs.TrySetException(new BanterDisconnectedException());
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _lifecycle.CancelAsync().ConfigureAwait(false);
        var connection = _connection;
        if (connection is not null)
        {
            try
            {
                var bye = _codec.CreateEnvelope(new ByePayload(null));
                await connection.SendFrameAsync(_codec.EncodeEnvelope(bye), CancellationToken.None)
                    .AsTask().WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            }
            catch
            {
                // Best-effort goodbye.
            }

            await connection.DisposeAsync().ConfigureAwait(false);
        }

        if (_sessionLoop is not null)
        {
            await _sessionLoop.ConfigureAwait(false);
        }

        if (_keepAliveLoop is not null)
        {
            await _keepAliveLoop.ConfigureAwait(false);
        }

        _lifecycle.Dispose();
    }
}
