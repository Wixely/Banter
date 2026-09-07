using System.Text;
using System.Text.Json;

namespace Banter.App;

/// <summary>
/// Somewhere to protect a secret at rest, supplied by the head because the answer is per-platform.
///
/// <para>This is the seam <see cref="Banter.Client.Core.AgentKeyFile"/> describes and does not yet
/// fill: DPAPI on Windows, Keychain on macOS, libsecret on Linux. A head that supplies nothing
/// gets <see cref="None"/> and the file permissions underneath, which is a real defence against
/// another user on the box and no defence at all against anything running as this one.</para>
/// </summary>
public interface ISecretProtector
{
    byte[] Protect(byte[] plaintext);

    /// <summary>
    /// Reverses <see cref="Protect"/>. Throws when the bytes were protected by another user,
    /// another machine, or another protector — which is the case a caller must treat as
    /// "sign in again" rather than as corruption.
    /// </summary>
    byte[] Unprotect(byte[] protectedBytes);

    /// <summary>
    /// Stored as-is. Named honestly so nothing reads as protected when it is not, and so a head
    /// that has no OS store still works rather than losing the feature.
    /// </summary>
    public static ISecretProtector None { get; } = new PassthroughProtector();

    private sealed class PassthroughProtector : ISecretProtector
    {
        public byte[] Protect(byte[] plaintext) => plaintext;

        public byte[] Unprotect(byte[] protectedBytes) => protectedBytes;
    }
}

/// <summary>
/// The account this client signs in with, kept so that starting it again does not mean typing a
/// password again.
///
/// <para><b>Why this is not in <see cref="BanterSettings"/>.</b> That file is preferences — plain
/// JSON, safe to read, safe to copy between machines, safe to put in a dotfiles repository. This
/// one is a credential: written user-only, wrapped by whatever <see cref="ISecretProtector"/> the
/// head supplies, and worthless on another machine when that protector is DPAPI. Keeping them
/// apart means the rule for each file is one sentence long instead of a caveat.</para>
///
/// <para>A password is held rather than a server-issued token because there is nothing else to
/// hold: humans authenticate with a password on every connect (agents are the ones with keys).
/// A token endpoint would be the better answer and is a protocol change, not a client one.</para>
/// </summary>
public sealed record StoredCredentials(string Server, string User, string Password)
{
    /// <summary>Beside the settings file, so a profile is still one folder.</summary>
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Banter", "credentials.dat");

    /// <summary>
    /// Where credentials live for a given settings file. Debug configurations point
    /// <c>--settings</c> at a scratch profile and would otherwise all share one credential file
    /// in the real profile — so alice's password would be sitting in bob's session.
    /// </summary>
    public static string PathFor(string? settingsPath) =>
        string.IsNullOrWhiteSpace(settingsPath)
            ? DefaultPath
            : Path.Combine(
                Path.GetDirectoryName(Path.GetFullPath(settingsPath)) ?? ".",
                Path.GetFileNameWithoutExtension(settingsPath) + ".credentials.dat");

    /// <summary>
    /// Loads what was stored, or null when there is nothing to load or it cannot be read.
    ///
    /// <para>Unreadable is not an error worth reporting loudly: a protector that has changed, a
    /// profile copied to another machine, or a half-written file all mean the same thing to the
    /// person in front of it — sign in again. The reason still goes to <paramref name="problem"/>
    /// for anyone looking.</para>
    /// </summary>
    public static StoredCredentials? Load(
        string? path = null, ISecretProtector? protector = null, Action<string>? problem = null)
    {
        path ??= DefaultPath;
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var plain = (protector ?? ISecretProtector.None).Unprotect(File.ReadAllBytes(path));
            var stored = JsonSerializer.Deserialize<StoredCredentials>(Encoding.UTF8.GetString(plain));
            return stored is { Server.Length: > 0, User.Length: > 0, Password.Length: > 0 } ? stored : null;
        }
        catch (Exception ex)
        {
            problem?.Invoke($"could not read {path}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Writes the credential, readable by this user alone. Returns false on failure.</summary>
    public bool TrySave(string? path = null, ISecretProtector? protector = null, Action<string>? problem = null)
    {
        path ??= DefaultPath;
        try
        {
            if (Path.GetDirectoryName(Path.GetFullPath(path)) is { Length: > 0 } directory)
            {
                Directory.CreateDirectory(directory);
            }

            var plain = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(this));
            File.WriteAllBytes(path, (protector ?? ISecretProtector.None).Protect(plain));

            // Set after writing, and only where it means something. On Windows the file inherits
            // the profile's ACL, which already excludes other users; on POSIX a default umask
            // would otherwise leave a password world-readable.
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            return true;
        }
        catch (Exception ex)
        {
            problem?.Invoke($"could not write {path}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Removes the stored credential. Signing out has to leave nothing behind, so a failure to
    /// delete is worth reporting rather than swallowing — the alternative is telling somebody
    /// they are signed out while their password is still on disk.
    /// </summary>
    public static bool TryDelete(string? path = null, Action<string>? problem = null)
    {
        path ??= DefaultPath;
        try
        {
            // Already gone is the goal, not a failure. File.Delete is silent about a missing file
            // but throws DirectoryNotFoundException when the profile folder itself is absent,
            // which is the ordinary case of signing out having never signed in.
            if (!File.Exists(path))
            {
                return true;
            }

            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problem?.Invoke($"could not remove {path}: {ex.Message}");
            return false;
        }
    }
}
