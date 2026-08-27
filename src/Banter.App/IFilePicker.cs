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
}

/// <summary>
/// Cannot pick. The default, so the app runs headlessly and on a head that has wired no dialog,
/// rather than needing a null check at every call site.
/// </summary>
public sealed class NullFilePicker : IFilePicker
{
    public static NullFilePicker Instance { get; } = new();

    public bool IsSupported => false;

    public Task<string?> PickAsync(string title, CancellationToken cancellationToken = default) =>
        Task.FromResult<string?>(null);
}

/// <summary>Answers with whatever the test set. Used by the tests, and by a head that wants to log.</summary>
public sealed class StubFilePicker(string? answer) : IFilePicker
{
    public bool IsSupported { get; init; } = true;

    public List<string> Titles { get; } = [];

    public Task<string?> PickAsync(string title, CancellationToken cancellationToken = default)
    {
        Titles.Add(title);
        return Task.FromResult(answer);
    }
}
