using System.Security.Cryptography;
using Banter.App;

namespace Banter.App.Desktop;

/// <summary>
/// Wraps a secret with DPAPI, scoped to the current Windows user.
///
/// <para>What this buys: the stored password is unreadable by every other account on the machine
/// and by anyone who takes the disk or a backup, because the key is derived from this user's
/// credentials and never leaves the machine. What it does not buy — and no user-scoped store on
/// any platform does — is protection from code already running as this user, which can simply ask
/// DPAPI to unwrap it in turn. That is the honest ceiling for "remember my password" in a desktop
/// application without a master password.</para>
///
/// <para>The entropy is a constant rather than a per-install random value on purpose. A random one
/// would have to be stored beside the ciphertext to be usable, where it protects nothing, and
/// losing it would lock somebody out of their own credential; a constant makes the wrapping
/// specific to this application without pretending to be a second factor.</para>
///
/// <para>Guarded at run time rather than with <c>[SupportedOSPlatform("windows")]</c> on the type.
/// The attribute is the more precise tool, but it applies to every member including the factory
/// below, whose whole job is to be callable on a platform where this does not work.</para>
/// </summary>
public sealed class DpapiSecretProtector : ISecretProtector
{
    private static readonly byte[] Entropy = "Banter.App.Desktop/credentials/v1"u8.ToArray();

    public byte[] Protect(byte[] plaintext)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("DPAPI is Windows-only; use ISecretProtector.None.");
        }

        return ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser);
    }

    public byte[] Unprotect(byte[] protectedBytes)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("DPAPI is Windows-only; use ISecretProtector.None.");
        }

        return ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
    }

    /// <summary>
    /// The protector for this machine: DPAPI on Windows, nothing anywhere else.
    ///
    /// <para>Returning <see cref="ISecretProtector.None"/> on Linux rather than refusing to
    /// remember anything is deliberate — the file is still user-only, which is what
    /// <see cref="Banter.Client.Core.AgentKeyFile"/> already relies on for a private key. Wiring
    /// libsecret is the same per-platform job named there, and this is the seam it plugs into.
    /// </para>
    /// </summary>
    public static ISecretProtector ForThisMachine() =>
        OperatingSystem.IsWindows() ? new DpapiSecretProtector() : ISecretProtector.None;

    /// <summary>
    /// How well a remembered password is actually held here, in words that belong on screen
    /// rather than in a log. Shown on the sign-in screen so staying signed in is a decision made
    /// with the facts rather than an assumption.
    /// </summary>
    public static string StorageDescription() =>
        OperatingSystem.IsWindows()
            ? "Kept for next time, encrypted for your Windows account."
            : "Kept for next time in a file only your user can read.";
}
