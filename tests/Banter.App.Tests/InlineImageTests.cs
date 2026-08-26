using System.Text.Json;
using Banter.App;
using SkiaSharp;
using Xunit;
using Xunit.Abstractions;

namespace Banter.App.Tests;

/// <summary>
/// Images shown inline in the timeline. As with multi-line text, these measure what actually got
/// laid out and painted: a test that only checked the model would pass while the timeline showed
/// nothing.
/// </summary>
public sealed class InlineImageTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "banter-img-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best effort */ }
    }

    /// <summary>A real PNG on disk, in a colour nothing else in the UI uses.</summary>
    private string WritePng(int width, int height)
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, $"{Guid.NewGuid():N}.png");
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(new SKColor(255, 140, 0));
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    private static ChatViewModel Room()
    {
        var vm = new ChatViewModel();
        vm.SetNick("alice");
        vm.AddRoom("#main");
        return vm;
    }

    /// <summary>Pixels of the image's distinctive orange actually painted by the app.</summary>
    private static long OrangePixels(ChatViewModel vm)
    {
        var app = new BanterChatApp(vm);
        using var doc = app.CreateDocument();
        doc.BuildDisplayList(1100, 760);
        var px = doc.RenderToPixels(1100, 760, SKColors.Black);

        long count = 0;
        for (var i = 0; i + 3 < px.Length; i += 4)
        {
            if (px[i] > 200 && px[i + 1] is > 100 and < 190 && px[i + 2] < 60)
            {
                count++;
            }
        }

        return count;
    }

    private static double RowHeight(ChatViewModel vm)
    {
        var app = new BanterChatApp(vm);
        using var doc = app.CreateDocument();
        doc.BuildDisplayList(1100, 760);
        using var dump = JsonDocument.Parse(doc.DebugDump(1100, 760));
        return Tallest(dump.RootElement.GetProperty("tree"));
    }

    private static double Tallest(JsonElement node)
    {
        var height = 0.0;
        if (node.TryGetProperty("class", out var cls) && cls.GetString() is { } name &&
            name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("line") &&
            node.TryGetProperty("box", out var box))
        {
            height = box[3].GetDouble();
        }

        if (node.TryGetProperty("children", out var children))
        {
            foreach (var child in children.EnumerateArray())
            {
                height = Math.Max(height, Tallest(child));
            }
        }

        return height;
    }

    [Fact]
    public void AnImageAttachmentIsActuallyPainted()
    {
        var vm = Room();
        vm.Append("#main", "bob", "here you go", 0, fileId: "f1");
        vm.SetAttachmentInfo("f1", "shot.png", 4096);

        Assert.Equal(0, OrangePixels(vm));   // nothing until the image is fetched

        vm.SetInlineImage("f1", WritePng(64, 32));

        var painted = OrangePixels(vm);
        output.WriteLine($"orange pixels painted: {painted}");
        Assert.True(painted > 1000, $"The image should be drawn; only {painted} pixels matched.");
    }

    [Fact]
    public void AnImageMakesItsRowTaller()
    {
        var withoutImage = Room();
        withoutImage.Append("#main", "bob", "here you go", 0, fileId: "f1");

        var withImage = Room();
        withImage.Append("#main", "bob", "here you go", 0, fileId: "f1");
        withImage.SetInlineImage("f1", WritePng(64, 32));

        Assert.True(
            RowHeight(withImage) > RowHeight(withoutImage),
            "A row showing an image should be taller than the same row without one.");
    }

    [Fact]
    public void AspectRatioIsPreservedSoImagesAreNotSquashed()
    {
        var wide = Room();
        wide.Append("#main", "bob", "wide", 0, fileId: "f1");
        wide.SetInlineImage("f1", WritePng(320, 80));

        var tall = Room();
        tall.Append("#main", "bob", "tall", 0, fileId: "f2");
        tall.SetInlineImage("f2", WritePng(320, 320));

        // Width is fixed by CSS and height follows the source, so a square image is much taller.
        Assert.True(RowHeight(tall) > RowHeight(wide) * 2, "A square image should be far taller than a wide one.");
    }

    [Fact]
    public void RowsWithoutAnImageStayHidden()
    {
        var vm = Room();

        var row = vm.Append("#main", "bob", "just text", 0);

        Assert.Equal("", row.ImageSrc);
        Assert.Contains("hidden", row.ImageClass);
    }

    [Fact]
    public void AnAttachmentThatIsNotAnImageStaysAChip()
    {
        var vm = Room();

        // Only SetInlineImage reveals the element, and the session only calls it for images -
        // a preview of a zip would be a grey box.
        var row = vm.Append("#main", "bob", "the report", 0, fileId: "f1");
        vm.SetAttachmentInfo("f1", "report.pdf", 2048);

        Assert.Contains("hidden", row.ImageClass);
        Assert.Equal("attach", row.AttachClass);
    }

    [Fact]
    public void TheSameImageInTwoRoomsIsShownInBoth()
    {
        var vm = Room();
        vm.AddRoom("#other");
        var a = vm.Append("#main", "bob", "shared", 0, fileId: "f1");
        var b = vm.Append("#other", "bob", "shared again", 0, fileId: "f1");

        vm.SetInlineImage("f1", WritePng(32, 32));

        Assert.Equal("inline-image", a.ImageClass);
        Assert.Equal("inline-image", b.ImageClass);
        Assert.Equal(a.ImageSrc, b.ImageSrc);
    }

    [Fact]
    public void TheSourceIsAFileUriBecauseThatIsWhatTheEngineResolves()
    {
        var vm = Room();
        var row = vm.Append("#main", "bob", "x", 0, fileId: "f1");
        var path = WritePng(8, 8);

        vm.SetInlineImage("f1", path);

        // A bare Windows path is not a resource the engine will load.
        Assert.StartsWith("file:///", row.ImageSrc);
    }

    [Fact]
    public void AMissingImageFileDoesNotBreakTheTimeline()
    {
        var vm = Room();
        vm.Append("#main", "bob", "here you go", 0, fileId: "f1");
        vm.SetInlineImage("f1", Path.Combine(_dir, "never-written.png"));

        // A cache file deleted underneath us should cost a picture, not the room.
        var app = new BanterChatApp(vm);
        using var doc = app.CreateDocument();
        doc.BuildDisplayList(1100, 760);
        var pixels = doc.RenderToPixels(1100, 760, SKColors.Black);

        Assert.Contains(pixels, b => b != 0);
    }

    [Theory]
    [InlineData("image/png", true)]
    [InlineData("image/jpeg", true)]
    [InlineData("application/pdf", false)]
    [InlineData("text/plain", false)]
    public void OnlyImagesQualifyForAPreview(string mime, bool isImage) =>
        Assert.Equal(isImage, MimeTypes.IsImage(mime));
}
