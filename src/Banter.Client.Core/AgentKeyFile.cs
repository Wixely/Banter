namespace Banter.Client.Core;

/// <summary>
/// Reading and writing an agent's private key on the machine that owns it.
///
/// <para><b>This is file permissions, not encryption.</b> The key is written so that other users on
/// the box cannot read it, which is worth having — it defends against a shared machine and against
/// a backup or a stolen disk. It does not defend against anything running as the agent's own user,
/// because the agent itself must be able to read the key. Wrapping it in DPAPI, Keychain or
/// libsecret would raise the floor and is a per-platform job; this seam is where that goes.</para>
/// </summary>
public static class AgentKeyFile
{
    /// <summary>
    /// Writes the key, readable and writable by this user alone.
    ///
    /// <para>The mode is set on POSIX, where it is meaningful and where a default umask would
    /// otherwise leave the file world-readable. On Windows it is a no-op and the file inherits the
    /// profile's ACL, which already excludes other users.</para>
    /// </summary>
    public static async Task SaveAsync(string path, byte[] privateKey, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(privateKey);

        if (Path.GetDirectoryName(Path.GetFullPath(path)) is { Length: > 0 } directory)
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllBytesAsync(path, privateKey, cancellationToken).ConfigureAwait(false);

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    public static Task<byte[]> LoadAsync(string path, CancellationToken cancellationToken = default) =>
        File.ReadAllBytesAsync(path, cancellationToken);

    /// <summary>
    /// Whether the file both exists and looks like a key we could sign with. Checked before
    /// connecting so a truncated or empty file is reported as itself rather than as a login
    /// failure, which would send someone looking at the server.
    /// </summary>
    public static bool IsUsable(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            using var key = System.Security.Cryptography.ECDsa.Create();
            key.ImportPkcs8PrivateKey(File.ReadAllBytes(path), out _);
            return true;
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or IOException)
        {
            return false;
        }
    }
}
