using Android.Content;
using Android.Database;
using Android.Provider;
using Banter.App;
using Uri = Android.Net.Uri;

namespace Banter.App.Android;

/// <summary>
/// The Android half of <see cref="IFilePicker"/>: the system document picker, with the chosen
/// content copied somewhere the rest of the app can read it.
///
/// <para><b>Why a copy.</b> The seam is a path — <see cref="PickAsync"/> answers with one, and
/// <c>BanterChatSession.UploadAsync</c> does <c>File.Exists</c> and <c>File.ReadAllBytes</c> on it.
/// Android does not hand back paths. It hands back a <c>content://</c> URI belonging to whichever
/// app owns the file, readable only through a ContentResolver and only while the grant lasts. So
/// the choice is to widen the seam on every platform, or to land the bytes once here. Landing them
/// keeps <c>/upload</c>, the desktop head and the tests all speaking about files on disk.</para>
///
/// <para><b>The name is carried across deliberately.</b> The room shows
/// <c>Path.GetFileName(path)</c>, so a copy called <c>tmp3f9a.bin</c> would be uploaded under that
/// name and the sender would have no idea why. The display name is read from the resolver and used
/// for the copy, which is also why the copies go in a directory of their own: two files can
/// legitimately have the same name, and the cache is cleared per pick rather than accumulating a
/// gallery inside the app's private storage.</para>
/// </summary>
public sealed class AndroidFilePicker(MainActivity activity) : IFilePicker
{
    public bool IsSupported => true;

    public Task<string?> PickAsync(string title, CancellationToken cancellationToken = default)
    {
        // ActionOpenDocument rather than ActionGetContent: it is the long-lived, system-provided
        // picker (SAF), it can reach cloud providers as well as local storage, and it is what
        // Android has wanted apps to use since KitKat. CategoryOpenable excludes anything that
        // cannot actually be streamed.
        var intent = new Intent(Intent.ActionOpenDocument);
        intent.AddCategory(Intent.CategoryOpenable);
        intent.SetType("*/*");

        // CreateChooser is nullable in the bindings. It has no documented way of failing, but the
        // intent is the whole request, so there is nothing to ask for without one.
        var chooser = Intent.CreateChooser(intent, title);
        return chooser is null
            ? Task.FromResult<string?>(null)
            : activity.PickFileAsync(chooser, cancellationToken);
    }

    /// <summary>
    /// Copies what the picker chose into the app's cache and answers with the path. Null when the
    /// content cannot be opened at all — a provider that has gone away, or a grant already spent.
    /// </summary>
    internal static string? Materialise(Context context, Uri uri)
    {
        var name = DisplayName(context, uri) ?? "attachment";

        var dir = Path.Combine(context.CacheDir?.AbsolutePath ?? Path.GetTempPath(), "attachments");
        Directory.CreateDirectory(dir);

        // One pick at a time. Without this the directory becomes every file ever attached, in an
        // app-private location nothing prunes and nobody can see.
        foreach (var stale in Directory.EnumerateFiles(dir))
        {
            try
            {
                File.Delete(stale);
            }
            catch (IOException)
            {
                // Held open by an upload still running. It will be cleared by the next pick.
            }
        }

        var path = Path.Combine(dir, name);
        using var source = context.ContentResolver?.OpenInputStream(uri);
        if (source is null)
        {
            return null;
        }

        using var destination = File.Create(path);
        source.CopyTo(destination);
        return path;
    }

    /// <summary>
    /// The name the user knows the file by. <c>IOpenableColumns.DisplayName</c> is the only part of
    /// a content URI that is meant to be shown to a person; the URI's own last segment is a
    /// provider-internal id and is frequently just a number.
    /// </summary>
    private static string? DisplayName(Context context, Uri uri)
    {
        using ICursor? cursor = context.ContentResolver?.Query(
            uri, [IOpenableColumns.DisplayName], selection: null, selectionArgs: null, sortOrder: null);

        if (cursor is null || !cursor.MoveToFirst())
        {
            return null;
        }

        var column = cursor.GetColumnIndex(IOpenableColumns.DisplayName);
        var name = column >= 0 ? cursor.GetString(column) : null;
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        // A provider can say ANYTHING here, and this value becomes a path. It is another
        // application's string, not the platform's: take the leaf, drop what cannot be in a
        // filename, and then refuse the three results that are not names at all. Without the last
        // check a provider answering ".." walks the copy out of the cache directory, and one
        // answering "/" or "???" leaves an empty string that Path.Combine resolves to the
        // directory itself — which File.Create then fails on.
        name = Path.GetFileName(name);
        var safe = string.Join("_", name.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
        return safe is "" or "." or ".." ? null : safe;
    }
}
