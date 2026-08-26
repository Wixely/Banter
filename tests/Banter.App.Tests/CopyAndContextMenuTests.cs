using Banter.App;
using CupriFace.Interaction;
using SkiaSharp;
using Xunit;

namespace Banter.App.Tests;

/// <summary>
/// Copying text and images out of the timeline. CupriFace raises <c>ContextRequested</c> and
/// leaves the clipboard to the host, so everything here ends in a call through
/// <see cref="IClipboard"/> — which is what these assert on.
/// </summary>
public sealed class CopyAndContextMenuTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "banter-copy-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private string WritePng()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "shot.png");
        using var bitmap = new SKBitmap(8, 8);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(new SKColor(255, 140, 0));
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    private static (BanterChatApp App, ChatViewModel Vm, RecordingClipboard Clip) Build(bool images = true)
    {
        var vm = new ChatViewModel();
        vm.SetNick("alice");
        vm.AddRoom("#main");
        var clip = new RecordingClipboard { SupportsImages = images };
        return (new BanterChatApp(vm) { Clipboard = clip }, vm, clip);
    }

    [Fact]
    public void CopyingWithNoSelectionFallsBackToTheNewestMessage()
    {
        var (app, vm, clip) = Build();
        vm.Append("#main", "bob", "first", 0);
        vm.Append("#main", "dagger", "most recent", 0);
        using var doc = app.CreateDocument();
        doc.BuildDisplayList(1100, 760);

        app.CopySelection();

        // "Copy" doing nothing at all reads as a broken menu.
        Assert.Equal("most recent", Assert.Single(clip.Texts));
    }

    [Fact]
    public void CopyingAnEmptyRoomDoesNotPutRubbishOnTheClipboard()
    {
        var (app, _, clip) = Build();
        using var doc = app.CreateDocument();
        doc.BuildDisplayList(1100, 760);

        app.CopySelection();

        Assert.Empty(clip.Texts);
    }

    [Fact]
    public void AMultiLineMessageIsCopiedWithItsNewlinesIntact()
    {
        var (app, vm, clip) = Build();
        vm.Append("#main", "dagger", "line one\nline two\nline three", 0);
        using var doc = app.CreateDocument();
        doc.BuildDisplayList(1100, 760);

        app.CopySelection();

        // Copying a code block and pasting it as one run-on line would be useless.
        Assert.Equal("line one\nline two\nline three", Assert.Single(clip.Texts));
    }

    [Fact]
    public void CopyingAnImagePutsTheBitmapOnTheClipboard()
    {
        var (app, vm, clip) = Build();
        var path = WritePng();
        vm.Append("#main", "bob", "here", 0, fileId: "f1");
        vm.SetInlineImage("f1", path);
        using var doc = app.CreateDocument();
        doc.BuildDisplayList(1100, 760);

        app.CopyMostRecentImage();

        Assert.Equal(path, Assert.Single(clip.Images));
        Assert.Empty(clip.Texts);
    }

    [Fact]
    public void WhereThePlatformCannotCopyABitmapThePathGoesOnAsText()
    {
        var (app, vm, clip) = Build(images: false);
        vm.Append("#main", "bob", "here", 0, fileId: "f1");
        var path = WritePng();
        vm.SetInlineImage("f1", path);
        using var doc = app.CreateDocument();
        doc.BuildDisplayList(1100, 760);

        app.CopyMostRecentImage();

        // Something pasteable beats nothing happening.
        Assert.Empty(clip.Images);
        Assert.Equal(path, Assert.Single(clip.Texts));
    }

    [Fact]
    public void CopyingAnImageWhenThereIsNoneDoesNothing()
    {
        var (app, vm, clip) = Build();
        vm.Append("#main", "bob", "just text", 0);
        using var doc = app.CreateDocument();
        doc.BuildDisplayList(1100, 760);

        app.CopyMostRecentImage();

        Assert.Empty(clip.Images);
        Assert.Empty(clip.Texts);
    }

    [Fact]
    public void TheEnginesOwnCopyCommandReachesTheClipboard()
    {
        var (app, vm, clip) = Build();
        vm.Append("#main", "bob", "from the text-field menu", 0);
        using var doc = app.CreateDocument();
        doc.BuildDisplayList(1100, 760);

        // Right-clicking a text field raises this; the engine does not touch the clipboard.
        doc.RequestContextCommand(ContextCommand.Copy);

        Assert.Equal("from the text-field menu", Assert.Single(clip.Texts));
    }

    [Fact]
    public void TheContextMenuItemsAreRealElementsInTheMarkup()
    {
        var (app, vm, _) = Build();
        vm.Append("#main", "bob", "x", 0);
        using var doc = app.CreateDocument();
        doc.BuildDisplayList(1100, 760);

        // A handler bound to a selector that does not exist is silently dead, so check the
        // markup actually declares them.
        Assert.Contains("cupri-context-menu", app.Html);
        Assert.Contains("copy-selection", app.Html);
        Assert.Contains("copy-image", app.Html);
    }

    [Fact]
    public void CopyingTheRoomNameWorks()
    {
        var (app, vm, clip) = Build();
        vm.SwitchTo("#main");
        using var doc = app.CreateDocument();
        doc.BuildDisplayList(1100, 760);

        app.Clipboard.SetText(vm.Model.ActiveRoom);

        Assert.Equal("#main", Assert.Single(clip.Texts));
    }

    [Fact]
    public void TheDefaultClipboardIsHarmless()
    {
        // The app must run headlessly and on a host that wired nothing.
        var vm = new ChatViewModel();
        vm.AddRoom("#main");
        vm.Append("#main", "bob", "x", 0, fileId: "f1");
        vm.SetInlineImage("f1", WritePng());
        var app = new BanterChatApp(vm);
        using var doc = app.CreateDocument();
        doc.BuildDisplayList(1100, 760);

        app.CopySelection();
        app.CopyMostRecentImage();

        Assert.False(NullClipboard.Instance.TrySetImage("anything"));
    }
}
