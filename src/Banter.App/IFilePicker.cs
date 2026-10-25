namespace Banter.App;

/// <summary>
/// Choosing a file to send, which is a per-platform concern.
///
/// <para>CupriFace has no native picker — that is why <c>/upload &lt;path&gt;</c> exists and why it
/// works identically on every head. This is the affordance on top of it: the slash command stays,
/// because typing a path is still the fastest way when you know it, and because a head that cannot
/// open a dialog keeps working.</para>
/// </summary>
public interface IFilePicker
{
    /// <summary>
    /// Whether this platform can open a dialog. The app hides its attach control when false
    /// rather than offering a button that does nothing.
    /// </summary>
    bool IsSupported { get; }

    /// <summary>
    /// Asks the user for a file. Null when they cancelled, or when the platform cannot ask —
    /// cancelling is the common case and is not an error, so it is a return value and not an
    /// exception.
    /// </summary>
    Task<string?> PickAsync(string title, CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether this platform can ask where to put a file. The other direction from
    /// <see cref="PickAsync"/>, and the one a phone cannot do without: a file written into the
    /// app's own directory there is a file nobody can get out again.
    /// </summary>
    bool CanSave { get; }

    /// <summary>
    /// Asks the user where to save <paramref name="content"/>, suggesting <paramref name="name"/>,
    /// and writes it there. Answers with where it went as the user would recognise it - a path, or
    /// on a phone the document's name - or null when they backed out, which is not an error.
    /// </summary>
    Task<string?> SaveAsync(string name, string mimeType, byte[] content, string title, CancellationToken cancellationToken = default);
}

/// <summary>Where to put a file: the save half of <see cref="IFilePicker"/> as a delegate, for the
/// session to hold without knowing the head. Null means the person backed out.</summary>
public delegate Task<string?> SaveFileAsync(string name, string mimeType, byte[] content, string title);

/// <summary>
/// Cannot pick. The default, so the app runs headlessly and on a head that has wired no dialog,
/// rather than needing a null check at every call site.
/// </summary>
public sealed class NullFilePicker : IFilePicker
{
    public static NullFilePicker Instance { get; } = new();

    public bool IsSupported => false;

    public bool CanSave => false;

    public Task<string?> PickAsync(string title, CancellationToken cancellationToken = default) =>
        Task.FromResult<string?>(null);

    public Task<string?> SaveAsync(string name, string mimeType, byte[] content, string title, CancellationToken cancellationToken = default) =>
        Task.FromResult<string?>(null);
}

/// <summary>Answers with whatever the test set. Used by the tests, and by a head that wants to log.</summary>
public sealed class StubFilePicker(string? answer) : IFilePicker
{
    public bool IsSupported { get; init; } = true;

    public bool CanSave { get; init; } = true;

    /// <summary>What a save answers with; null is the user backing out.</summary>
    public string? SavedAs { get; init; } = "saved-here";

    public List<string> Titles { get; } = [];

    /// <summary>Every save asked for: the suggested name, the type, and the bytes.</summary>
    public List<(string Name, string MimeType, byte[] Content)> Saves { get; } = [];

    public Task<string?> PickAsync(string title, CancellationToken cancellationToken = default)
    {
        Titles.Add(title);
        return Task.FromResult(answer);
    }

    public Task<string?> SaveAsync(string name, string mimeType, byte[] content, string title, CancellationToken cancellationToken = default)
    {
        Titles.Add(title);
        Saves.Add((name, mimeType, content));
        return Task.FromResult(SavedAs);
    }
}
