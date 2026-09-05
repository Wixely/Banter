using System.Security.Cryptography;

namespace Banter.Protocol;

/// <summary>
/// The signing keys an agent identity rests on: P-256 ECDSA, which the base class library has
/// everywhere, so no head takes a new dependency to hold an identity.
///
/// <para>The one rule the whole model depends on: <see cref="Generate"/> is called on the agent's
/// machine and only the public half is ever sent. Nothing here serialises a private key onto a
/// wire, and nothing should be added that does.</para>
/// </summary>
public static class AgentKeys
{
    /// <summary>
    /// What the agent signs. The nonce alone would be enough against replay, but binding the nick
    /// in as well means a signature captured from one agent cannot be presented as another's on a
    /// server that reused a nonce.
    /// </summary>
    public static byte[] ChallengeBytes(string username, byte[] nonce)
    {
        ArgumentNullException.ThrowIfNull(nonce);
        var name = System.Text.Encoding.UTF8.GetBytes(username.ToLowerInvariant());
        var bytes = new byte[name.Length + 1 + nonce.Length];
        name.CopyTo(bytes, 0);
        bytes[name.Length] = 0;                     // a separator, so "ab" + "c" cannot equal "a" + "bc"
        nonce.CopyTo(bytes, name.Length + 1);
        return bytes;
    }

    /// <summary>A fresh keypair. Call this where the agent will run, and nowhere else.</summary>
    public static (byte[] PublicKey, byte[] PrivateKey) Generate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (key.ExportSubjectPublicKeyInfo(), key.ExportPkcs8PrivateKey());
    }

    public static byte[] Sign(byte[] privateKey, ReadOnlySpan<byte> data)
    {
        using var key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(privateKey, out _);
        return key.SignData(data, HashAlgorithmName.SHA256);
    }

    /// <summary>
    /// Whether <paramref name="signature"/> is this key's. Returns false rather than throwing on a
    /// malformed key or signature: both arrive from the network, and a bad one is a failed login,
    /// not a server fault.
    /// </summary>
    public static bool Verify(byte[] publicKey, ReadOnlySpan<byte> data, byte[] signature)
    {
        if (publicKey is null || signature is null)
        {
            return false;
        }

        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(publicKey, out _);
            return key.VerifyData(data, signature, HashAlgorithmName.SHA256);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    /// <summary>
    /// Whether these bytes are a usable P-256 public key, checked when one is offered at enrolment
    /// so a broken key is refused while somebody is watching rather than at the next login.
    /// </summary>
    public static bool IsUsablePublicKey(byte[]? publicKey)
    {
        if (publicKey is null || publicKey.Length == 0)
        {
            return false;
        }

        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(publicKey, out _);
            return key.KeySize == 256;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    /// <summary>Groups of four so it can be read aloud over a desk: "3f2a 91c0 ...".</summary>
    public static string Fingerprint(byte[] publicKey)
    {
        var digest = SHA256.HashData(publicKey);
        var hex = Convert.ToHexStringLower(digest.AsSpan(0, 8));
        return string.Join(' ', Enumerable.Range(0, 4).Select(i => hex.Substring(i * 4, 4)));
    }
}
