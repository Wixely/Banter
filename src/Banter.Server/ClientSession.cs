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
    Files.FileStore files,
    IAgentIdentityStore? identities = null)
{
    private readonly Channel<byte[]> _outbox = Channel.CreateUnbounded<byte[]>(
        new UnboundedChannelOptions { SingleReader = true });

    public string Nick { get; private set; } = "";
    public bool IsAgent { get; private set; }

    /// <summary>Operator account: added to every room an agent opens (PLAN §8a).</summary>
    public bool IsAdmin { get; private set; }

    /// <summary>
    /// Attributes this agent announced (PLAN §8a), or null if it has not announced. Held on the
    /// session so a re-join re-applies them without the agent having to announce again, and only
    /// ever written by the room engine on the single-writer loop.
    /// </summary>
    public AgentAnnouncePayload? Announcement { get; set; }
    /// <summary>The banter.core ordinal agreed with this peer during HELLO (CupriMark).</summary>
    public ushort NegotiatedCoreVersion { get; private set; } = 1;
    private bool Authenticated => Nick.Length > 0;

    /// <summary>
    /// The nonce this session last issued, or null when none is outstanding. One at a time and
    /// cleared the moment it is answered, so a signature is good for exactly one attempt.
    /// </summary>
    private byte[]? _challenge;

    private string _challengeFor = "";

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
                case HelloPayload hello:
                    if (!BanterCatalog.TryNegotiateCore(hello.Ranges, out var negotiated))
                    {
                        Send(new ErrorPayload(
                            "VERSION_MISMATCH",
                            $"No mutually supported {BanterCatalog.CoreComponent} revision (server speaks {BanterCatalog.SupportedCore})."),
                            replyTo: envelope.MsgId);
                        return; // no common protocol revision — nothing further to say
                    }

                    NegotiatedCoreVersion = negotiated;
                    Send(new HelloPayload(
                        "Banter.Server",
                        typeof(ClientSession).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
                        ["banter.core"],
                        BanterCatalog.LocalRanges()),
                        replyTo: envelope.MsgId);
                    break;

                case AuthPayload auth:
                    await HandleAuthAsync(envelope, auth, cancellationToken).ConfigureAwait(false);
                    break;

                case AuthChallengePayload challenge:
                    HandleAuthChallenge(envelope, challenge);
                    break;

                case AuthKeyPayload key:
                    await HandleAuthKeyAsync(envelope, key, cancellationToken).ConfigureAwait(false);
                    break;

                // Before authentication on purpose: an agent redeeming a code has no credential
                // yet. The code IS the claim, and it is spent on use.
                case AgentEnrolPayload enrol:
                    await HandleEnrolAsync(envelope, enrol, cancellationToken).ConfigureAwait(false);
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

                    if (await TryHandleIdentityAsync(envelope, payload, cancellationToken).ConfigureAwait(false))
                    {
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

    /// <summary>
    /// Answers with a nonce to sign. Deliberately answered the same way whether or not the account
    /// exists: a challenge that only arrived for real agents would let anyone enumerate them.
    /// </summary>
    private void HandleAuthChallenge(BanterEnvelope envelope, AuthChallengePayload payload)
    {
        if (Authenticated)
        {
            Send(new ErrorPayload("ALREADY_AUTHENTICATED", "This session is already authenticated."), replyTo: envelope.MsgId);
            return;
        }

        _challenge = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        _challengeFor = payload.Username;
        Send(new AuthChallengeIssuedPayload(_challenge), replyTo: envelope.MsgId);
    }

    private async Task HandleAuthKeyAsync(BanterEnvelope envelope, AuthKeyPayload payload, CancellationToken cancellationToken)
    {
        if (Authenticated)
        {
            Send(new ErrorPayload("ALREADY_AUTHENTICATED", "This session is already authenticated."), replyTo: envelope.MsgId);
            return;
        }

        // Taken before anything can fail, so one challenge answers exactly one attempt however it
        // turns out. A wrong signature costs a fresh round trip rather than another guess.
        var nonce = _challenge;
        var forUser = _challengeFor;
        _challenge = null;
        _challengeFor = "";

        if (nonce is null || !string.Equals(forUser, payload.Username, StringComparison.OrdinalIgnoreCase))
        {
            Send(new AuthFailPayload("Ask for a challenge first."), replyTo: envelope.MsgId);
            return;
        }

        var identity = identities is null
            ? null
            : await identities.FindAsync(payload.Username, cancellationToken).ConfigureAwait(false);

        if (identity?.PublicKey is not { } publicKey
            || !AgentKeys.Verify(publicKey, AgentKeys.ChallengeBytes(payload.Username, nonce), payload.Signature))
        {
            // One message for "no such agent", "not enrolled" and "wrong key": which of those it is
            // would tell an unauthenticated caller something it has not earned.
            Send(new AuthFailPayload("Invalid credentials."), replyTo: envelope.MsgId);
            return;
        }

        Nick = identity.Nick;
        IsAgent = true;
        IsAdmin = false;
        await engine.RegisterAsync(this).ConfigureAwait(false);
        Send(new AuthOkPayload(Guid.NewGuid().ToString("N"), Nick, IsAgent, IsAdmin), replyTo: envelope.MsgId);
    }

    private async Task HandleEnrolAsync(BanterEnvelope envelope, AgentEnrolPayload payload, CancellationToken cancellationToken)
    {
        if (identities is null)
        {
            Send(new ErrorPayload("NO_IDENTITY_STORE", "This server does not keep agent identities."), replyTo: envelope.MsgId);
            return;
        }

        var (result, identity) = await identities
            .EnrolAsync(payload.Code, payload.PublicKey, DateTimeOffset.UtcNow, cancellationToken)
            .ConfigureAwait(false);

        switch (result)
        {
            case AgentEnrolmentResult.Enrolled when identity is not null:
                Send(Describe(identity), replyTo: envelope.MsgId);
                break;
            case AgentEnrolmentResult.Expired:
                Send(new ErrorPayload("CODE_EXPIRED", "That enrolment code has expired. Ask an admin for a fresh one."), replyTo: envelope.MsgId);
                break;
            case AgentEnrolmentResult.BadKey:
                Send(new ErrorPayload("BAD_KEY", "That is not a usable P-256 public key."), replyTo: envelope.MsgId);
                break;
            default:
                Send(new ErrorPayload("UNKNOWN_CODE", "That enrolment code is not valid. It may already have been used."), replyTo: envelope.MsgId);
                break;
        }
    }

    /// <summary>The admin-only identity verbs. Returns false when the payload was not one of them.</summary>
    private async Task<bool> TryHandleIdentityAsync(BanterEnvelope envelope, object payload, CancellationToken cancellationToken)
    {
        if (payload is not (AgentIdentityCreatePayload or AgentIdentityUpdatePayload or AgentIdentityDeletePayload
            or AgentIdentityListPayload or AgentIdentityReissuePayload))
        {
            return false;
        }

        if (!IsAdmin)
        {
            // Who may run an agent is an operator decision, and an agent that could create agents
            // could give itself whatever clearance it liked.
            Send(new ErrorPayload("NOT_ADMIN", "Only an admin may manage agent identities."), replyTo: envelope.MsgId);
            return true;
        }

        if (identities is null)
        {
            Send(new ErrorPayload("NO_IDENTITY_STORE", "This server does not keep agent identities."), replyTo: envelope.MsgId);
            return true;
        }

        var now = DateTimeOffset.UtcNow;
        switch (payload)
        {
            case AgentIdentityCreatePayload create:
            {
                if (await identities.FindAsync(create.Nick, cancellationToken).ConfigureAwait(false) is not null)
                {
                    Send(new ErrorPayload("NICK_TAKEN", $"There is already an agent called '{create.Nick}'."), replyTo: envelope.MsgId);
                    break;
                }

                var code = await identities.CreateAsync(
                    new AgentIdentity
                    {
                        Nick = create.Nick,
                        Rooms = create.Rooms,
                        Skills = create.Skills,
                        Locality = ParseLocality(create.Locality),
                        Clearance = ParseClearance(create.Clearance),
                    },
                    now,
                    cancellationToken).ConfigureAwait(false);

                Send(new AgentEnrolmentCodePayload(create.Nick, code, (now + IdentityWindow).ToUnixTimeSeconds()),
                    replyTo: envelope.MsgId);
                break;
            }

            case AgentIdentityReissuePayload reissue:
            {
                var code = await identities.ReissueAsync(reissue.Nick, now, cancellationToken).ConfigureAwait(false);
                if (code is null)
                {
                    Send(new ErrorPayload("NO_SUCH_AGENT", $"There is no agent called '{reissue.Nick}'."), replyTo: envelope.MsgId);
                    break;
                }

                Send(new AgentEnrolmentCodePayload(reissue.Nick, code, (now + IdentityWindow).ToUnixTimeSeconds()),
                    replyTo: envelope.MsgId);
                break;
            }

            case AgentIdentityUpdatePayload update:
            {
                var changed = await identities.UpdateAsync(
                    update.Nick, update.Rooms, update.Skills,
                    update.Locality is null ? null : ParseLocality(update.Locality),
                    update.Clearance is null ? null : ParseClearance(update.Clearance),
                    cancellationToken).ConfigureAwait(false);

                if (changed)
                {
                    Send(new OkPayload(), replyTo: envelope.MsgId);
                }
                else
                {
                    Send(new ErrorPayload("NO_SUCH_AGENT", $"There is no agent called '{update.Nick}'."), replyTo: envelope.MsgId);
                }

                break;
            }

            case AgentIdentityDeletePayload delete:
            {
                var removed = await identities.DeleteAsync(delete.Nick, cancellationToken).ConfigureAwait(false);
                if (!removed)
                {
                    Send(new ErrorPayload("NO_SUCH_AGENT", $"There is no agent called '{delete.Nick}'."), replyTo: envelope.MsgId);
                    break;
                }

                Send(new OkPayload(), replyTo: envelope.MsgId);
                break;
            }

            case AgentIdentityListPayload:
            {
                var all = await identities.ListAsync(cancellationToken).ConfigureAwait(false);
                Send(new AgentIdentitiesPayload([.. all.Select(Describe)]), replyTo: envelope.MsgId);
                break;
            }
        }

        return true;
    }

    /// <summary>Mirrors the store's own window, so the admin is told the truth about the code.</summary>
    private static readonly TimeSpan IdentityWindow = TimeSpan.FromHours(1);

    private static AgentIdentityPayload Describe(AgentIdentity identity) => new(
        identity.Nick,
        identity.Rooms,
        identity.Skills,
        identity.Locality.ToString().ToLowerInvariant(),
        identity.Clearance.ToString().ToLowerInvariant(),
        identity.PublicKey is not null,
        identity.KeyFingerprint,
        identity.EnrolmentPending);

    private static AgentLocality ParseLocality(string value) =>
        Enum.TryParse<AgentLocality>(value, ignoreCase: true, out var parsed) ? parsed : AgentLocality.Local;

    private static DataSensitivity ParseClearance(string value) =>
        Enum.TryParse<DataSensitivity>(value, ignoreCase: true, out var parsed) ? parsed : DataSensitivity.Sensitive;

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
        IsAdmin = account.IsAdmin;
        await engine.RegisterAsync(this).ConfigureAwait(false);
        Send(new AuthOkPayload(Guid.NewGuid().ToString("N"), Nick, IsAgent, IsAdmin), replyTo: envelope.MsgId);
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
