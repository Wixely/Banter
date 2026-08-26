using Banter.App.Desktop;
using SkiaSharp;
using Xunit;

namespace Banter.App.Tests;

/// <summary>
/// The <c>CF_DIB</c> byte layout. Hand-written interop with a header and a row order is exactly
/// the sort of thing that is silently wrong — an upside-down paste or swapped colour channels
/// looks like a working feature until someone pastes into Paint.
/// </summary>
public sealed class ClipboardDibTests
{
    private const int HeaderSize = 40;

    /// <summary>A bitmap whose corners differ, so row and channel order are both observable.</summary>
    private static SKBitmap Corners(int width, int height)
    {
        var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Black);
        bitmap.SetPixel(0, 0, new SKColor(255, 0, 0));                       // top-left red
        bitmap.SetPixel(width - 1, height - 1, new SKColor(0, 0, 255));      // bottom-right blue
        return bitmap;
    }

    [Fact]
    public void TheHeaderDescribesTheBitmap()
    {
        using var bitmap = Corners(4, 3);

        var dib = DibImage.ToDib(bitmap);

        Assert.Equal(HeaderSize, BitConverter.ToInt32(dib, 0));
        Assert.Equal(4, BitConverter.ToInt32(dib, 4));                        // width
        Assert.Equal(3, BitConverter.ToInt32(dib, 8));                        // height, positive
        Assert.Equal(1, BitConverter.ToInt16(dib, 12));                       // planes
        Assert.Equal(32, BitConverter.ToInt16(dib, 14));                      // bits per pixel
        Assert.Equal(0, BitConverter.ToInt32(dib, 16));                       // BI_RGB, uncompressed
        Assert.Equal(4 * 3 * 4, BitConverter.ToInt32(dib, 20));               // image byte count
    }

    [Fact]
    public void TheBufferIsExactlyHeaderPlusPixels()
    {
        using var bitmap = Corners(7, 5);

        var dib = DibImage.ToDib(bitmap);

        // A buffer even one byte short is read past its end by whatever pastes it.
        Assert.Equal(HeaderSize + (7 * 4 * 5), dib.Length);
    }

    [Fact]
    public void RowsAreStoredBottomUpBecauseTheHeightIsPositive()
    {
        using var bitmap = Corners(2, 2);

        var dib = DibImage.ToDib(bitmap);

        // A positive biHeight means the FIRST row in the buffer is the BOTTOM row of the image.
        // Getting this backwards is the classic upside-down paste.
        var firstRow = HeaderSize;
        var bottomRight = firstRow + 4;    // second pixel of the first stored row
        Assert.Equal(255, dib[bottomRight + 0]);   // blue channel of the blue corner
        Assert.Equal(0, dib[bottomRight + 2]);     // its red channel

        var lastRow = HeaderSize + (2 * 4);
        Assert.Equal(255, dib[lastRow + 2]);       // red channel of the top-left corner
        Assert.Equal(0, dib[lastRow + 0]);
    }

    [Fact]
    public void ChannelsAreStoredAsBgraNotRgba()
    {
        using var bitmap = new SKBitmap(1, 1, SKColorType.Rgba8888, SKAlphaType.Premul);
        bitmap.SetPixel(0, 0, new SKColor(10, 20, 30));   // r=10 g=20 b=30

        var dib = DibImage.ToDib(bitmap);

        // CF_DIB is little-endian BGRA; writing RGBA swaps red and blue on every paste.
        Assert.Equal(30, dib[HeaderSize + 0]);
        Assert.Equal(20, dib[HeaderSize + 1]);
        Assert.Equal(10, dib[HeaderSize + 2]);
    }

    [Fact]
    public void ASourceThatIsAlreadyBgraIsHandledToo()
    {
        // The cache holds whatever the sender uploaded, so both colour types turn up.
        using var bitmap = new SKBitmap(1, 1, SKColorType.Bgra8888, SKAlphaType.Premul);
        bitmap.SetPixel(0, 0, new SKColor(10, 20, 30));

        var dib = DibImage.ToDib(bitmap);

        Assert.Equal(HeaderSize + 4, dib.Length);
        Assert.Equal(30, dib[HeaderSize + 0]);
        Assert.Equal(10, dib[HeaderSize + 2]);
    }

    [Fact]
    public void ARealisticScreenshotSizedImageConvertsWithoutTrouble()
    {
        using var bitmap = Corners(1920, 1080);

        var dib = DibImage.ToDib(bitmap);

        Assert.Equal(HeaderSize + (1920 * 4 * 1080), dib.Length);
    }
}
