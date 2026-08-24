using System.Collections.Concurrent;
using Banter.Protocol;
using Banter.Protocol.Transport;

namespace Banter.Client.Core;

/// <summary>
/// The client runtime: connect + handshake + auth, request/response correlation over
/// <c>msgId</c>/<c>replyTo</c>, and server pushes surfaced as events. UI layers (CLI, CupriFace
/// app) and the agent SDK all sit on this.
/// </summary>
public sealed class BanterClient : IAsyncDisposable
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    private readonly IBanterConnection _connection;
    private readonly BanterCodec _codec = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<object?>> _pending = new();
    private Task? _receiveLoop;
    private bool _disposed;

    private BanterClient(IBanterConnection connection) => _connection = connection;

    public string Nick { get; private set; } = "";
    public bool IsAgent { get; private set; }
    public string SessionId { get; private set; } = "";

    public event Action<MsgPayload>? MessageReceived;
    public event Action<PrivMsgPayload>? PrivateMessageReceived;
    public event Action<JoinPayload>? MemberJoined;
    public event Action<PartPayload>? MemberParted;
    public event Action<TopicPayload>? TopicChanged;
    public event Action? Disconnected;

    public static async Task<BanterClient> ConnectAsync(
        IBanterClientTransport transport,
        Uri endpoint,
        string username,
        string secret,
        CancellationToken cancellationToken = default)
    {
        var connection = await transport.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
        var client = new BanterClient(connection);
        client._receiveLoop = Task.Run(client.ReceiveLoopAsync, CancellationToken.None);
        try
        {
            await client.RequestAsync<HelloPayload>(
                new HelloPayload("Banter.Client", typeof(BanterClient).Assembly.GetName().Version?.ToString(3) ?? "0.0.0", ["banter.core"]),
                cancellationToken).ConfigureAwait(false);

            var reply = await client.RequestRawAsync(new AuthPayload(username, secret, IsAgentToken: false), cancellationToken)
                .ConfigureAwait(false);
            switch (reply)
            {
                case AuthOkPayload ok:
                    client.Nick = ok.Nick;
                    client.IsAgent = ok.IsAgent;
                    client.SessionId = ok.SessionId;
                    return client;
                case AuthFailPayload fail:
                    throw new BanterAuthException(fail.Reason);
                default:
                    throw new BanterClientException($"Unexpected AUTH reply: {reply?.GetType().Name ?? "null"}.");
            }
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public Task JoinAsync(string room, CancellationToken cancellationToken = default) =>
        RequestAsync<OkPayload>(new JoinPayload(room), cancellationToken);

    public Task PartAsync(string room, string? reason = null, CancellationToken cancellationToken = default) =>
        RequestAsync<OkPayload>(new PartPayload(room, reason), cancellationToken);

    /// <summary>Fire-and-forget send; the authoritative message (id, timestamp) comes back as a
    /// <see cref="MessageReceived"/> echo to every member including this sender.</summary>
    public ValueTask SendMessageAsync(string room, string text, CancellationToken cancellationToken = default) =>
        SendAsync(_codec.CreateEnvelope(new MsgPayload(room, Nick, text, 0, null)), cancellationToken);

    public ValueTask SetTopicAsync(string room, string topic, CancellationToken cancellationToken = default) =>
        SendAsync(_codec.CreateEnvelope(new TopicPayload(room, topic)), cancellationToken);

    public Task<HistoryChunkPayload> GetHistoryAsync(
        string room, string? beforeMessageId = null, int limit = 50, CancellationToken cancellationToken = default) =>
        RequestAsync<HistoryChunkPayload>(new HistoryReqPayload(room, beforeMessageId, limit), cancellationToken);

    public Task<RoomListPayload> ListRoomsAsync(CancellationToken cancellationToken = default) =>
        RequestAsync<RoomListPayload>(new RoomListPayload([]), cancellationToken);

    public Task<RoomMembersPayload> GetMembersAsync(string room, CancellationToken cancellationToken = default) =>
        RequestAsync<RoomMembersPayload>(new RoomMembersPayload(room, []), cancellationToken);

    public async Task<TimeSpan> PingAsync(CancellationToken cancellationToken = default)
    {
        var sent = DateTimeOffset.UtcNow;
        await RequestAsync<PongPayload>(new PingPayload(sent.ToUnixTimeMilliseconds()), cancellationToken).ConfigureAwait(false);
        return DateTimeOffset.UtcNow - sent;
    }

    private async Task<TReply> RequestAsync<TReply>(object payload, CancellationToken cancellationToken)
        where TReply : class
    {
        var reply = await RequestRawAsync(payload, cancellationToken).ConfigureAwait(false);
        return reply switch
        {
            TReply typed => typed,
            ErrorPayload error => throw new BanterErrorException(error),
            _ => throw new BanterClientException(
                $"Expected {typeof(TReply).Name} in reply to {payload.GetType().Name}, got {reply?.GetType().Name ?? "null"}."),
        };
    }

    private async Task<object?> RequestRawAsync(object payload, CancellationToken cancellationToken)
    {
        var envelope = _codec.CreateEnvelope(payload);
        var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[envelope.MsgId] = tcs;
        try
        {
            await SendAsync(envelope, cancellationToken).ConfigureAwait(false);
            return await tcs.Task.WaitAsync(RequestTimeout, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(envelope.MsgId, out _);
        }
    }

    private ValueTask SendAsync(BanterEnvelope envelope, CancellationToken cancellationToken) =>
        _connection.SendFrameAsync(_codec.EncodeEnvelope(envelope), cancellationToken);

    private async Task ReceiveLoopAsync()
    {
        try
        {
            while (true)
            {
                var frame = await _connection.ReceiveFrameAsync().ConfigureAwait(false);
                if (frame is null)
                {
                    break;
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
                }
            }
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or InvalidDataException
            or ObjectDisposedException or OperationCanceledException or MessagePack.MessagePackSerializationException)
        {
            // Fall through to disconnect handling.
        }

        foreach (var pending in _pending)
        {
            if (_pending.TryRemove(pending.Key, out var tcs))
            {
                tcs.TrySetException(new BanterDisconnectedException());
            }
        }

        Disconnected?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            await SendAsync(_codec.CreateEnvelope(new ByePayload(null)), CancellationToken.None)
                .AsTask().WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort goodbye.
        }

        await _connection.DisposeAsync().ConfigureAwait(false);
        if (_receiveLoop is not null)
        {
            await _receiveLoop.ConfigureAwait(false);
        }
    }
}
