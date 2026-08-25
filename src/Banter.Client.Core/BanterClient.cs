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
}

/// <summary>
/// The client runtime: connect + handshake + auth, request/response correlation over
/// <c>msgId</c>/<c>replyTo</c>, server pushes surfaced as events, and automatic reconnect with
/// exponential backoff and room rejoin. UI layers (CLI, CupriFace app) and the agent SDK all
/// sit on this.
/// </summary>
public sealed class BanterClient : IAsyncDisposable
{
    private readonly IBanterClientTransport _transport;
    private readonly Uri _endpoint;
    private readonly string _username;
    private readonly string _secret;
    private readonly BanterClientOptions _options;
    private readonly BanterCodec _codec = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<object?>> _pending = new();
    private readonly HashSet<string> _joinedRooms = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _roomsLock = new();
    private readonly CancellationTokenSource _lifecycle = new();
    private volatile IBanterConnection? _connection;
    private Task? _sessionLoop;
    private bool _disposed;

    private BanterClient(IBanterClientTransport transport, Uri endpoint, string username, string secret, BanterClientOptions options)
    {
        _transport = transport;
        _endpoint = endpoint;
        _username = username;
        _secret = secret;
        _options = options;
    }

    public string Nick { get; private set; } = "";
    public bool IsAgent { get; private set; }
    public string SessionId { get; private set; } = "";
    /// <summary>The banter.core ordinal agreed with the server during HELLO (CupriMark).</summary>
    public ushort NegotiatedCoreVersion { get; private set; } = 1;
    public bool IsConnected => _connection is not null;

    public event Action<MsgPayload>? MessageReceived;
    public event Action<PrivMsgPayload>? PrivateMessageReceived;
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
        client._sessionLoop = Task.Run(client.RunSessionsAsync, CancellationToken.None);
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
    public Task<TaskListPayload> ListTasksAsync(
        string room, bool includeFinished = false, CancellationToken cancellationToken = default) =>
        RequestAsync<TaskListPayload>(new TaskListPayload(room, [], includeFinished), cancellationToken);

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

    /// <summary>Uploads content to a room. Deduplicated server-side by hash — a second upload
    /// of identical bytes completes without sending chunks. Unless <paramref name="quiet"/>,
    /// the server announces the file in the room as a message carrying the file reference.</summary>
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
        var start = await RequestAsync<FileInfoPayload>(
            new FilePutStartPayload(room, name, mimeType, content.Length, sha, description, quiet), cancellationToken)
            .ConfigureAwait(false);
        if (start.Complete)
        {
            return start;
        }

        for (var offset = 0; offset < content.Length; offset += UploadChunkBytes)
        {
            var slice = content[offset..Math.Min(offset + UploadChunkBytes, content.Length)];
            await RequestAsync<OkPayload>(new FilePutChunkPayload(start.FileId, offset, slice.ToArray()), cancellationToken)
                .ConfigureAwait(false);
        }

        return await RequestAsync<FileInfoPayload>(new FilePutEndPayload(start.FileId), cancellationToken).ConfigureAwait(false);
    }

    public async Task<byte[]> DownloadFileAsync(string fileId, CancellationToken cancellationToken = default)
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
        await RequestAsync<PongPayload>(new PingPayload(sent.ToUnixTimeMilliseconds()), cancellationToken).ConfigureAwait(false);
        return DateTimeOffset.UtcNow - sent;
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

            var reply = await RawRequestAsync(connection, new AuthPayload(_username, _secret, IsAgentToken: false), cancellationToken)
                .ConfigureAwait(false);
            switch (reply)
            {
                case AuthOkPayload ok:
                    Nick = ok.Nick;
                    IsAgent = ok.IsAgent;
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
            FailPending();
            if (_disposed || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            Disconnected?.Invoke();
            if (!_options.AutoReconnect)
            {
                return;
            }

            var next = await RedialWithBackoffAsync(cancellationToken).ConfigureAwait(false);
            if (next is null)
            {
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
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
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
                await RequestAsync<OkPayload>(new JoinPayload(room), cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // A failed rejoin (room deleted, connection dropped again) shouldn't kill the
                // others; the next disconnect cycle retries.
            }
        }

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
                    case MsgPayload msg:
                        MessageReceived?.Invoke(msg);
                        break;
                    case PrivMsgPayload priv:
                        PrivateMessageReceived?.Invoke(priv);
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

    private async Task<TReply> RequestAsync<TReply>(object payload, CancellationToken cancellationToken)
        where TReply : class
    {
        var envelope = _codec.CreateEnvelope(payload);
        var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[envelope.MsgId] = tcs;
        try
        {
            await SendAsync(envelope, cancellationToken).ConfigureAwait(false);
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

    private async ValueTask SendAsync(BanterEnvelope envelope, CancellationToken cancellationToken)
    {
        var connection = _connection ?? throw new BanterDisconnectedException();
        try
        {
            await connection.SendFrameAsync(_codec.EncodeEnvelope(envelope), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            throw new BanterDisconnectedException();
        }
    }

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

        _lifecycle.Dispose();
    }
}
