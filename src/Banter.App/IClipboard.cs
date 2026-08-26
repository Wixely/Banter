namespace Banter.App;

/// <summary>
/// Writing to the system clipboard, which is a per-platform concern.
///
/// <para>CupriFace raises <c>ContextRequested</c> and leaves the clipboard to the host, and its
/// own clipboard hook is string-only — so images need real platform code. That lives in the host
/// head; <see cref="BanterChatApp"/> only ever calls this.</para>
/// </summary>
public interface IClipboard
{
    void SetText(string text);

    /// <summary>
    /// Put an image on the clipboard so it can be pasted into another application. Returns false
    /// when the platform cannot — the caller falls back to copying the path, which is worth more
    /// than doing nothing silently.
    /// </summary>
    bool TrySetImage(string filePath);
}

/// <summary>
/// Does nothing. The default so <see cref="BanterChatApp"/> runs headlessly in tests and on a
/// host that has not wired a clipboard, rather than needing a null check at every call site.
/// </summary>
public sealed class NullClipboard : IClipboard
{
    public static NullClipboard Instance { get; } = new();

    public void SetText(string text)
    {
        // Deliberately nothing.
    }

    public bool TrySetImage(string filePath) => false;
}

/// <summary>Records what was copied. Used by the tests, and useful for a host that wants to log.</summary>
public sealed class RecordingClipboard : IClipboard
{
    public List<string> Texts { get; } = [];
    public List<string> Images { get; } = [];

    /// <summary>When false, <see cref="TrySetImage"/> reports failure — the platform-cannot case.</summary>
    public bool SupportsImages { get; init; } = true;

    public void SetText(string text) => Texts.Add(text);

    public bool TrySetImage(string filePath)
    {
        if (!SupportsImages)
        {
            return false;
        }

        Images.Add(filePath);
        return true;
    }
}
