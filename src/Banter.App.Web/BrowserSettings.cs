using System.Globalization;
using System.Runtime.InteropServices;

namespace Banter.App.Web;

/// <summary>
/// The preferences a browser session should not make you choose twice, held in
/// <c>localStorage</c>.
///
/// <para>The desktop head has <c>BanterSettings</c> and a file; this is the same idea with the
/// only storage a page has. What it deliberately does NOT hold is anything the file version keeps
/// out too, plus one thing the file version is allowed: <b>the server link</b>. A link carries a
/// node's ICE credentials and has a lifetime, so one remembered past its node's is a client that
/// hangs partway through a handshake with nothing to talk to — which reads as the transport being
/// broken. The page is offered a live link by the node that served it, or a person pastes one;
/// neither wants a stale answer arriving first.</para>
///
/// <para>Reads are cached for the life of the page. localStorage is synchronous and blocks the
/// thread the frame loop runs on, and the zoom is read once at boot but written on every notch of
/// a Ctrl+wheel.</para>
/// </summary>
internal static class BrowserSettings
{
    private const string ZoomKey = "banter.zoom";
    private const string UserKey = "banter.user";

    /// <summary>Long enough for anything stored here; a value longer than this was not written by
    /// us and is not worth honouring.</summary>
    private const int MaxChars = 256;

    private static float? _zoom;
    private static string? _user;

    /// <summary>
    /// The zoom this browser was last left at, or 1.
    ///
    /// <para>Clamped on the way in rather than trusted: this comes from storage any script on the
    /// origin can write, and a zero or a NaN would lay the document out at an absurd size. The
    /// range is <see cref="CupriFace.PresentInfo"/>'s own.</para>
    /// </summary>
    public static float Zoom
    {
        get
        {
            if (_zoom is { } cached) return cached;
            var stored = Read(ZoomKey);
            var value = float.TryParse(stored, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                        && float.IsFinite(parsed)
                ? Math.Clamp(parsed, 0.25f, 4f)
                : 1f;
            _zoom = value;
            return value;
        }
        set
        {
            if (_zoom is { } current && Math.Abs(current - value) < 0.001f) return;
            _zoom = value;
            Write(ZoomKey, value.ToString("0.###", CultureInfo.InvariantCulture));
        }
    }

    /// <summary>The nick last connected with, so the sign-in screen is not blank every visit. Not
    /// a secret, and deliberately separate from the password, which is never stored.</summary>
    public static string User
    {
        get => _user ??= Read(UserKey) ?? "";
        set
        {
            if (_user == value) return;
            _user = value;
            Write(UserKey, value);
        }
    }

    private static unsafe string? Read(string key)
    {
        var buffer = new char[MaxChars];
        fixed (char* k = key)
        fixed (char* b = buffer)
        {
            var length = StoreGet(k, b, buffer.Length);
            return length < 0 ? null : new string(buffer, 0, length);
        }
    }

    private static unsafe void Write(string key, string value)
    {
        fixed (char* k = key)
        fixed (char* v = value)
        {
            StoreSet(k, v);
        }
    }

    /// <summary>Returns the length in characters, or -1 when the key has never been set — which is
    /// not the same as an empty string.</summary>
    [DllImport("banterstore", EntryPoint = "banter_store_get")]
    private static extern unsafe int StoreGet(char* key, char* buffer, int capacityInChars);

    [DllImport("banterstore", EntryPoint = "banter_store_set")]
    private static extern unsafe void StoreSet(char* key, char* value);
}
