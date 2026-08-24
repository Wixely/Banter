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
    RoomEngine engine,
    Files.FileStore files)
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
        catch (Exception ex)
        {
            // A handler bug should not be silent — the session still ends, but leave a trace.
            Console.Error.WriteLine($"Session for '{Nick}' crashed: {ex}");
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

                    if (IsFilePayload(payload))
                    {
                        // File transfer runs on the session, not the engine loop — a 32 MB
                        // upload must never stall room fan-out. Membership checks round-trip
                        // into the engine where needed.
                        await HandleFileAsync(envelope, payload).ConfigureAwait(false);
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

    private static bool IsFilePayload(object payload) => payload is FilePutStartPayload or FilePutChunkPayload
        or FilePutEndPayload or FileGetPayload or FileListPayload or FileInfoPayload
        or FileGrantPayload or FileRevokePayload or FileDeletePayload;

    private async Task HandleFileAsync(BanterEnvelope envelope, object payload)
    {
        try
        {
            switch (payload)
            {
                case FilePutStartPayload start:
                    if (!await engine.IsMemberAsync(this, start.Room).ConfigureAwait(false))
                    {
                        Send(new ErrorPayload("NOT_IN_ROOM", $"You are not in {start.Room}."), replyTo: envelope.MsgId);
                        return;
                    }

                    var (startInfo, quiet) = await files.StartUploadAsync(Nick, start).ConfigureAwait(false);
                    if (startInfo.Complete && !quiet)
                    {
                        await engine.AnnounceFileAsync(this, start.Room, startInfo.FileId, startInfo.Name).ConfigureAwait(false);
                    }

                    Send(startInfo, replyTo: envelope.MsgId);
                    return;

                case FilePutChunkPayload chunk:
                    await files.AppendChunkAsync(Nick, chunk).ConfigureAwait(false);
                    Send(new OkPayload(), replyTo: envelope.MsgId);
                    return;

                case FilePutEndPayload end:
                    var (finalInfo, room, wasQuiet) = await files.FinalizeAsync(Nick, end.FileId).ConfigureAwait(false);
                    if (!wasQuiet)
                    {
                        await engine.AnnounceFileAsync(this, room, finalInfo.FileId, finalInfo.Name).ConfigureAwait(false);
                    }

                    Send(finalInfo, replyTo: envelope.MsgId);
                    return;

                case FileGetPayload get:
                    await RequireAccessAsync(get.FileId).ConfigureAwait(false);
                    Send(await files.ReadChunkAsync(get.FileId, get.Offset, get.MaxBytes).ConfigureAwait(false), replyTo: envelope.MsgId);
                    return;

                case FileListPayload list:
                    if (!await engine.IsMemberAsync(this, list.Room).ConfigureAwait(false))
                    {
                        Send(new ErrorPayload("NOT_IN_ROOM", $"You are not in {list.Room}."), replyTo: envelope.MsgId);
                        return;
                    }

                    Send(new FileListPayload(list.Room, await files.ListForRoomAsync(list.Room).ConfigureAwait(false)), replyTo: envelope.MsgId);
                    return;

                case FileInfoPayload infoRequest:
                    await RequireAccessAsync(infoRequest.FileId).ConfigureAwait(false);
                    var info = await files.GetInfoAsync(infoRequest.FileId).ConfigureAwait(false)
                        ?? throw new Files.FileStoreException("NOT_FOUND", "No such file.");
                    Send(info, replyTo: envelope.MsgId);
                    return;

                case FileGrantPayload grant:
                    if (!Core.RoomName.IsValid(grant.Room))
                    {
                        Send(new ErrorPayload("BAD_ROOM", $"'{grant.Room}' is not a valid room name."), replyTo: envelope.MsgId);
                        return;
                    }

                    await files.GrantAsync(Nick, grant.FileId, grant.Room).ConfigureAwait(false);
                    Send(new OkPayload(), replyTo: envelope.MsgId);
                    return;

                case FileRevokePayload revoke:
                    await files.RevokeAsync(Nick, revoke.FileId, revoke.Room).ConfigureAwait(false);
                    Send(new OkPayload(), replyTo: envelope.MsgId);
                    return;

                case FileDeletePayload delete:
                    await files.DeleteAsync(Nick, delete.FileId).ConfigureAwait(false);
                    Send(new OkPayload(), replyTo: envelope.MsgId);
                    return;
            }
        }
        catch (Files.FileStoreException ex)
        {
            Send(new ErrorPayload(ex.Code, ex.Message), replyTo: envelope.MsgId);
        }
    }

    /// <summary>Room-based visibility (PLAN §5a / open question 5): access requires membership
    /// of at least one granted room — uploaders who left a room lose access like anyone else.</summary>
    private async Task RequireAccessAsync(string fileId)
    {
        var granted = await files.GetGrantedRoomsAsync(fileId).ConfigureAwait(false);
        var mine = await engine.GetMemberRoomsAsync(this).ConfigureAwait(false);
        if (!granted.Intersect(mine, StringComparer.OrdinalIgnoreCase).Any())
        {
            throw new Files.FileStoreException("NO_ACCESS", "You are not in any room this file is shared with.");
        }
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
