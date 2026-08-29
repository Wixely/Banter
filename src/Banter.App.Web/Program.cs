using System.Runtime.InteropServices.JavaScript;
using Banter.App;
using Banter.App.Web;
using Banter.Client.Core;
using Banter.Transport.Shrine;
using CupriFace;
using CupriNet.Alembic.BouncyCastle;
using CupriNet.Vessel;
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
    private static BanterChatSession? _session;

    /// <summary>Matches the desktop head's default; there is no settings file to read one from.</summary>
    private const int HistoryPageSize = 50;

    /// <summary>Builds the app and its document once, before the first frame.</summary>
    [JSExport]
    internal static void Init()
    {
        _viewModel = new ChatViewModel();

        // Every callback resolves the session when it is called rather than when it is wired: the
        // app exists from the first frame, and the session only after someone connects. The desktop
        // head can build them in the other order because it connects before it has a window.
        _app = new BanterChatApp(_viewModel)
        {
            ConnectAsync = ConnectAsync,
            SendAsync = (room, text) => _session?.SendAsync(room, text) ?? Task.CompletedTask,
            // Room switching is local: the backlog is held per room and history was filled at join.
            RoomSelected = _ => { },
            LoadOlderAsync = room => _session?.LoadOlderAsync(room, HistoryPageSize) ?? Task.CompletedTask,
            CommandAsync = (room, text) => _session?.CommandAsync(room, text) ?? Task.CompletedTask,
            DownloadAsync = id => _session?.DownloadAsync(id) ?? Task.CompletedTask,
            JoinRoomAsync = room => _session?.JoinAsync(room, HistoryPageSize) ?? Task.CompletedTask,
            ToolsOpenAsync = filter => _session?.LoadToolsAsync(filter) ?? Task.CompletedTask,
            ToolsSaveAsync = (agent, tools) => _session?.SaveToolsAsync(agent, tools) ?? Task.CompletedTask,
        };
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
        // from — so the connect screen is where every session starts. The server is the node's
        // intonation link: pasted in, or seeded by a node that was asked to leave one.
        _viewModel.ShowConnect(server: SeedLink(), user: "");
    }

    /// <summary>
    /// What the Connect button does. The whole of the web head's networking: a WebRTC DataChannel
    /// becomes a vessel, the vessel carries a Pilgrimage, the Pilgrimage carries a conduit, and
    /// every Banter verb above that is the same code the desktop runs.
    /// </summary>
    private static async Task ConnectAsync(string server, string user, string password)
    {
        try
        {
            var transport = new ShrineClientTransport(
                async (intonation, cancellationToken) =>
                {
                    var channel = await BrowserDataChannel.ConnectAsync(intonation, cancellationToken);
                    return new DataChannelVessel(channel);
                },
                new BouncyCastleSuite());

            var client = await BanterClient.ConnectAsync(transport, new Uri(server.Trim()), user, password);

            var session = new BanterChatSession(client, _viewModel);
            _session = session;
            _viewModel.Connected();

            // The status badge starts at "Disconnected" and only a head moves it. Without this the
            // room opens looking broken while working perfectly.
            _viewModel.Post(() =>
            {
                _viewModel.SetNick(client.Nick);
                _viewModel.SetStatus("Connected", connected: true);
            });

            try
            {
                await session.JoinAsync("#main", HistoryPageSize);
            }
            catch (Exception ex)
            {
                _viewModel.Post(() => _viewModel.System("#main", $"could not join #main: {ex.Message}"));
            }

            // Probe once, so the tools control appears only for an account the server would let
            // manage them.
            await session.LoadToolsAsync("");
        }
        catch (Exception ex)
        {
            // Shown on the connect card rather than logged: in a browser there is no console the
            // person is looking at, and a button that silently does nothing is the worst outcome.
            _viewModel.ConnectFailed(ex.Message);
        }
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

    /// <summary>
    /// A message arrived on the WebRTC data channel. Declared here rather than on
    /// <c>BrowserDataChannel</c> because the runtime groups exports by declaring type, and JS holds
    /// one handle — this one.
    /// </summary>
    /// <para>A plain <c>byte[]</c>, not a <c>MemoryView</c>: a view can only be round-tripped when
    /// C# created it, so it is the right marshalling outbound and an assertion failure inbound.
    /// This copies, which for chat-sized frames is not worth a shared buffer to avoid.</para>
    [JSExport]
    internal static void RtcMessage(byte[] message) => BrowserDataChannel.Deliver(message);

    /// <summary>The WebRTC data channel closed.</summary>
    [JSExport]
    internal static void RtcClosed() => BrowserDataChannel.NotifyClosed();

    /// <summary>
    /// A node published its link after the page had already loaded. Offered rather than imposed:
    /// the connect screen keeps whatever the person has typed.
    /// </summary>
    [JSExport]
    internal static void SeedArrived(string link) => _viewModel?.SuggestConnectServer(link);

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

    /// <summary>The link a node left for us, or empty. Read once, at startup.</summary>
    [JSImport("seedLink", "banter")]
    internal static partial string SeedLink();
}
