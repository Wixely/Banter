using System.Runtime.InteropServices.JavaScript;
using Banter.App;
using CupriFace;
using SkiaSharp;

// The web head. A raw .NET WebAssembly host — no Blazor — running the same CupriApp the desktop
// and Android heads run, painted to a CPU Skia surface that main.js blits onto a <canvas>.
//
// Deliberately smaller than CupriFace's own WebWasm sample, which is the reference for all of
// this. That sample carries video underlays, an ARIA mirror, IME positioning and a clipboard
// bridge; this carries what Banter needs to be usable and nothing it does not, because every line
// here is upstream's concern living in the wrong repository until there is a CupriFace.Web
// package (CupriFace#73).
Console.WriteLine("[Banter] WASM runtime started.");

public partial class Interop
{
    private static ChatViewModel _viewModel = null!;
    private static CupriApp _app = null!;
    private static CupriDocument _doc = null!;
    private static SKColor _background;
    private static float _scale = 1f;
    private static SKBitmap? _bitmap;
    private static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();
    private static double _lastRefresh, _lastAnimMs;
    private static int _lastWidth, _lastHeight;
    private static bool _dirty = true;
    private static string _cursor = "";

    /// <summary>Builds the app and its document once, before the first frame.</summary>
    [JSExport]
    internal static void Init()
    {
        _viewModel = new ChatViewModel();
        _app = new BanterChatApp(_viewModel);
        _doc = _app.CreateDocument();

        // The wasm Skia build has exactly one embedded face (Noto Mono). Without registering a real
        // sans face the whole UI silently renders monospaced — the first family registered becomes
        // the generic sans target.
        foreach (var resource in new[] { "fonts.NotoSans-Regular.ttf", "fonts.NotoSans-Bold.ttf" })
        {
            using var stream = typeof(Interop).Assembly.GetManifestResourceStream(resource)!;
            var buffer = new byte[stream.Length];
            stream.ReadExactly(buffer);
            _doc.LoadFont(buffer);
        }

        _background = _app.Background;

        if (_app.IconDataUri is { } favicon)
        {
            SetFavicon(favicon);
        }

        // A link in a message opens in a new tab. Internal routing stays with the engine, exactly
        // as on the desktop.
        _doc.Navigated += e =>
        {
            if (e.External)
            {
                OpenUrl(e.Href);
            }
        };

        // No server is configured in a browser — there are no command-line arguments to read one
        // from — so the connect screen is where every session starts.
        _viewModel.ShowConnect(server: "", user: "");
    }

    /// <summary>
    /// Called every animation frame. Paints only when something changed: after input, on the app's
    /// own re-bind cadence, or throttled while an element is animating. An idle room costs nothing.
    /// </summary>
    [JSExport]
    internal static bool Tick(int width, int height, double nowMs)
    {
        if (_doc is null || width <= 0 || height <= 0)
        {
            return false;
        }

        if (width != _lastWidth || height != _lastHeight)
        {
            _lastWidth = width;
            _lastHeight = height;
            _dirty = true;
        }

        // A remote image (an inline attachment preview) finished loading.
        if (_doc.ConsumeImageArrived())
        {
            _dirty = true;
        }

        // The app's own cadence is what drains the view model's queue, so this is not merely a
        // repaint: skipping it would strand every message that arrived from the network.
        if (_app.RefreshIntervalSeconds > 0 &&
            Clock.Elapsed.TotalSeconds - _lastRefresh >= _app.RefreshIntervalSeconds)
        {
            _lastRefresh = Clock.Elapsed.TotalSeconds;
            _doc.Refresh();
            _dirty = true;
        }

        var animating = _doc.HasActiveAnimations;
        if (animating && nowMs - _lastAnimMs >= 33)
        {
            _lastAnimMs = nowMs;
            _dirty = true;
        }

        if (!_dirty)
        {
            return false;
        }

        _dirty = false;
        return Paint(width, height, animating);
    }

    private static bool Paint(int width, int height, bool animating)
    {
        var present = _app.Present(width, height);
        _scale = present.Scale <= 0 ? 1f : present.Scale;

        if (_bitmap is null || _bitmap.Width != width || _bitmap.Height != height)
        {
            _bitmap?.Dispose();
            _bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        }

        SKRectI? damage;
        using (var canvas = new SKCanvas(_bitmap))
        {
            if (animating)
            {
                _doc.Animate(Clock.Elapsed.TotalSeconds);
            }

            if (_scale == 1f)
            {
                // The bitmap keeps last frame's pixels, so the engine repaints only what changed
                // and tells us when the frame is identical.
                damage = _doc.RenderIncremental(canvas, present.LogicalWidth, present.LogicalHeight, _background);
            }
            else
            {
                // Under a scaled present the damage rect would not map 1:1, so repaint whole.
                canvas.Clear(_background);
                canvas.Save();
                canvas.Scale(_scale);
                _doc.Render(canvas, present.LogicalWidth, present.LogicalHeight);
                canvas.Restore();
                damage = new SKRectI(0, 0, width, height);
            }

            canvas.Flush();
        }

        if (damage is not { } rect)
        {
            return false;
        }

        // Zero-copy: JS gets a view over the bitmap's pixels in WASM memory. Reading `.Bytes`
        // instead would allocate and copy the whole surface every frame.
        unsafe
        {
            var pixels = new Span<byte>((void*)_bitmap.GetPixels(), _bitmap.ByteCount);
            Present(pixels, width, height, rect.Left, rect.Top, rect.Width, rect.Height);
        }

        return true;
    }

    // Input goes through the same dispatch the desktop head uses. Each Dispatch* reports whether
    // anything actually changed, and only then is a repaint worth doing — marking dirty on every
    // mouse-move repaints the whole canvas while the pointer crosses empty space.
    [JSExport]
    internal static void PointerDown(double x, double y, int clicks)
    {
        if (_doc?.DispatchClick(Logical(x), Logical(y), clicks) == true)
        {
            _dirty = true;
        }

        UpdateCursor(x, y);
    }

    [JSExport]
    internal static void PointerMove(double x, double y)
    {
        if (_doc?.DispatchPointerMove(Logical(x), Logical(y)) == true)
        {
            _dirty = true;
        }

        UpdateCursor(x, y);
    }

    [JSExport]
    internal static void PointerUp(double x, double y)
    {
        if (_doc?.DispatchPointerUp(Logical(x), Logical(y)) == true)
        {
            _dirty = true;
        }

        UpdateCursor(x, y);
    }

    [JSExport]
    internal static void Wheel(double x, double y, double dy)
    {
        // The browser reports wheel deltas in pixels, positive downward — the same direction
        // ScrollY grows, so unlike the desktop head there is no negation here.
        if (_doc?.DispatchWheel(Logical(x), Logical(y), (float)dy) == true)
        {
            _dirty = true;
        }
    }

    [JSExport]
    internal static void KeyChar(string text)
    {
        if (_doc?.DispatchKey(text, CupriFace.Interaction.EditKey.None) == true)
        {
            _dirty = true;
        }
    }

    [JSExport]
    internal static void EditKeyPress(int code, int mods)
    {
        if (_doc?.DispatchKey(null, (CupriFace.Interaction.EditKey)code, (CupriFace.Interaction.KeyMods)mods) == true)
        {
            _dirty = true;
        }
    }

    /// <summary>
    /// A Ctrl/Cmd chord. Returns whether the engine took it, so the page can suppress the browser's
    /// own shortcut only when it did — silently swallowing Ctrl+R or Ctrl+T would be worse than
    /// missing a chord.
    /// </summary>
    [JSExport]
    internal static bool KeyChord(string text, int mods)
    {
        var handled = _doc?.DispatchKey(text, CupriFace.Interaction.EditKey.None, (CupriFace.Interaction.KeyMods)mods) == true;
        if (handled)
        {
            _dirty = true;
        }

        return handled;
    }

    /// <summary>
    /// The engine's own wire codes for the keys main.js forwards. Exported rather than duplicated
    /// on the JS side, because a hand-copied ordinal table breaks silently when an enum member
    /// moves.
    /// </summary>
    [JSExport]
    internal static string EditKeyMap()
    {
        var keys = new (string Name, CupriFace.Interaction.EditKey Key)[]
        {
            ("Backspace", CupriFace.Interaction.EditKey.Backspace),
            ("Delete", CupriFace.Interaction.EditKey.Delete),
            ("ArrowLeft", CupriFace.Interaction.EditKey.Left),
            ("ArrowRight", CupriFace.Interaction.EditKey.Right),
            ("ArrowUp", CupriFace.Interaction.EditKey.Up),
            ("ArrowDown", CupriFace.Interaction.EditKey.Down),
            ("Home", CupriFace.Interaction.EditKey.Home),
            ("End", CupriFace.Interaction.EditKey.End),
            ("Enter", CupriFace.Interaction.EditKey.Enter),
            ("Escape", CupriFace.Interaction.EditKey.Escape),
            ("Tab", CupriFace.Interaction.EditKey.Tab),
            ("ShiftTab", CupriFace.Interaction.EditKey.ShiftTab),
            ("SelectAll", CupriFace.Interaction.EditKey.SelectAll),
        };

        return "{" + string.Join(",", keys.Select(k => $"\"{k.Name}\":{(int)k.Key}")) + "}";
    }

    [JSExport]
    internal static string? CopySelection() => _doc?.CopySelection();

    [JSExport]
    internal static string? CutSelection()
    {
        var text = _doc?.CutSelection();
        _dirty = true;
        return text;
    }

    private static float Logical(double value) => (float)(value / _scale);

    private static void UpdateCursor(double x, double y)
    {
        if (_doc is null)
        {
            return;
        }

        var css = CupriDocument.CursorCss(_doc.CursorAt(Logical(x), Logical(y)));
        if (css != _cursor)
        {
            _cursor = css;
            SetCursor(css);
        }
    }

    [JSImport("present", "banter")]
    internal static partial void Present(
        [JSMarshalAs<JSType.MemoryView>] Span<byte> rgba, int width, int height,
        int dx, int dy, int dw, int dh);

    [JSImport("cursor", "banter")]
    internal static partial void SetCursor(string name);

    [JSImport("navigate", "banter")]
    internal static partial void OpenUrl(string href);

    [JSImport("favicon", "banter")]
    internal static partial void SetFavicon(string dataUri);
}
