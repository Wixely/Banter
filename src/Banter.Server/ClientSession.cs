using System.Threading.Channels;
using Banter.Core;
using Banter.Protocol;
using Banter.Protocol.Transport;

namespace Banter.Server;

/// <summary>
/// One connected peer: owns the receive loop (session handshake, auth, ping; everything
/// room-shaped goes to the engine) and an outbound queue pumped to the connection so the
/// engine never blocks on a slow client.
/// </summary>
internal sealed class ClientSession(
    IBanterConnection connection,
    BanterCodec codec,
    IAccountStore accounts,
    RoomEngine engine)
{
    private readonly Channel<byte[]> _outbox = Channel.CreateUnbounded<byte[]>(
        new UnboundedChannelOptions { SingleReader = true });

    public string Nick { get; private set; } = "";
    public bool IsAgent { get; private set; }
    private bool Authenticated => Nick.Length > 0;

    public void Send<TPayload>(TPayload payload, string? replyTo = null) where TPayload : notnull =>
        _outbox.Writer.TryWrite(codec.EncodeEnvelope(codec.CreateEnvelope(payload, replyTo)));

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var sendPump = Task.Run(SendPumpAsync, CancellationToken.None);
        try
        {
            await ReceiveLoopAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or InvalidDataException
            or OperationCanceledException or ObjectDisposedException or MessagePack.MessagePackSerializationException)
        {
            // Disconnects and malformed peers end the session; nothing to escalate.
        }
        finally
        {
            _outbox.Writer.TryComplete();
            await sendPump.ConfigureAwait(false);
            if (Authenticated)
            {
                await engine.DisconnectAsync(this).ConfigureAwait(false);
            }

            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var frame = await connection.ReceiveFrameAsync(cancellationToken).ConfigureAwait(false);
            if (frame is null)
            {
                return;
            }

            var envelope = codec.DecodeEnvelope(frame);
            var payload = codec.DecodePayload(envelope);
            switch (payload)
            {
                case HelloPayload:
                    Send(new HelloPayload("Banter.Server", typeof(ClientSession).Assembly.GetName().Version?.ToString(3) ?? "0.0.0", ["banter.core"]),
                        replyTo: envelope.MsgId);
                    break;

                case AuthPayload auth:
                    await HandleAuthAsync(envelope, auth, cancellationToken).ConfigureAwait(false);
                    break;

                case PingPayload ping:
                    Send(new PongPayload(ping.Timestamp), replyTo: envelope.MsgId);
                    break;

                case ByePayload:
                    return;

                case null:
                    Send(new ErrorPayload("UNSUPPORTED", $"Message type {envelope.Type} has no contract on this server."),
                        replyTo: envelope.MsgId);
                    break;

                default:
                    if (!Authenticated)
                    {
                        Send(new ErrorPayload("UNAUTHENTICATED", "Authenticate before anything else."), replyTo: envelope.MsgId);
                        break;
                    }

                    await engine.DispatchAsync(this, envelope, payload).ConfigureAwait(false);
                    break;
            }
        }
    }

    private async Task HandleAuthAsync(BanterEnvelope envelope, AuthPayload auth, CancellationToken cancellationToken)
    {
        if (Authenticated)
        {
            Send(new ErrorPayload("ALREADY_AUTHENTICATED", "This session is already authenticated."), replyTo: envelope.MsgId);
            return;
        }

        var account = await accounts.AuthenticateAsync(auth.Username, auth.Secret, cancellationToken).ConfigureAwait(false);
        if (account is null)
        {
            Send(new AuthFailPayload("Invalid credentials."), replyTo: envelope.MsgId);
            return;
        }

        Nick = account.Username;
        IsAgent = account.IsAgent;
        await engine.RegisterAsync(this).ConfigureAwait(false);
        Send(new AuthOkPayload(Guid.NewGuid().ToString("N"), Nick, IsAgent), replyTo: envelope.MsgId);
    }

    private async Task SendPumpAsync()
    {
        try
        {
            await foreach (var frame in _outbox.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                await connection.SendFrameAsync(frame).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // The receive loop notices the dead connection and tears the session down.
        }
    }
}
