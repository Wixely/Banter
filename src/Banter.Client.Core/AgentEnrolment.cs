using Banter.Protocol;
using Banter.Protocol.Transport;

namespace Banter.Client.Core;

/// <summary>
/// The agent side of enrolment: redeem the one-time code an admin handed over, and keep the key
/// this machine made while doing it.
///
/// <para>The private key is generated here, on the machine that will run the agent, and is never
/// sent anywhere — only the public half goes to the server. That is what makes a captured code
/// worth so little: it buys one registration, and afterwards the thing that proves identity exists
/// only on this disk.</para>
/// </summary>
public static class AgentEnrolment
{
    /// <summary>
    /// Redeems <paramref name="code"/> against the server and returns the identity it claimed
    /// together with the private key to keep. Store the key somewhere the operating system
    /// protects — DPAPI on Windows, Keychain on macOS, libsecret on Linux — and never transmit it.
    /// </summary>
    /// <exception cref="InvalidOperationException">The server refused the code, saying why.</exception>
    public static async Task<(AgentIdentityPayload Identity, byte[] PrivateKey)> EnrolAsync(
        IBanterClientTransport transport,
        Uri endpoint,
        string code,
        CancellationToken cancellationToken = default)
    {
        var (publicKey, privateKey) = AgentKeys.Generate();

        await using var connection = await transport.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
        var codec = new BanterCodec();

        // No HELLO and no AUTH: enrolment is what happens before this machine has anything to
        // authenticate with, and the code is the entirety of the claim.
        var request = codec.CreateEnvelope(new AgentEnrolPayload(code, publicKey));
        await connection.SendFrameAsync(codec.EncodeEnvelope(request), cancellationToken).ConfigureAwait(false);

        while (await connection.ReceiveFrameAsync(cancellationToken).ConfigureAwait(false) is { } frame)
        {
            var envelope = codec.DecodeEnvelope(frame);
            if (envelope.ReplyTo != request.MsgId)
            {
                continue;
            }

            return codec.DecodePayload(envelope) switch
            {
                AgentIdentityPayload identity => (identity, privateKey),
                ErrorPayload error => throw new InvalidOperationException($"{error.Code}: {error.Message}"),
                _ => throw new InvalidOperationException("The server answered enrolment with something unexpected."),
            };
        }

        throw new InvalidOperationException("The server closed the connection without answering.");
    }
}
