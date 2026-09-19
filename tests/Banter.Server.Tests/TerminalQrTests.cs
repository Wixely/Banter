using Banter.Server;
using QRCoder;
using Xunit;

namespace Banter.Server.Tests;

/// <summary>
/// The QR a server prints so a phone can read its link off the screen.
///
/// <para>A real link, not a short string, because the length is the whole problem: ~380 characters
/// of base64 decides the version, the version decides the width, and the width decides whether
/// this fits in a terminal window at all.</para>
/// </summary>
public sealed class TerminalQrTests
{
    /// <summary>
    /// A link of the shape and length banter-nodestar prints. Mixed case and digits on purpose:
    /// the first draft of this fixture was 360 repeated 'A's, which QR encodes in alphanumeric
    /// mode and fits into a smaller symbol than any real link ever would — a test that agreed
    /// with itself about a width no user would see.
    /// </summary>
    private static readonly string Link = "cuprinet://intone/" + Base64Like(360);

    private static string Base64Like(int length)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
        return string.Create(length, alphabet, (span, chars) =>
        {
            for (var i = 0; i < span.Length; i++)
            {
                span[i] = chars[(i * 7) % chars.Length];
            }
        });
    }

    [Fact]
    public void ALinkFitsAcrossAnOrdinaryTerminal()
    {
        // 77 modules for the version a link of this length produces, plus the four-module quiet
        // zone on each side. Wider than 80 by five, which is why the server says so before it
        // prints rather than letting a narrow window shred it into noise.
        Assert.Equal(85, TerminalQr.Columns(Link));
    }

    [Fact]
    public void TwoModuleRowsShareALine()
    {
        var lines = TerminalQr.Render(Link, colour: false).TrimEnd('\n', '\r').Split('\n');

        // 85 rows of modules, two to a line, so the last line carries one row and half a blank.
        Assert.Equal(43, lines.Length);
    }

    [Fact]
    public void WithoutColourEveryCellIsABlockOrASpace()
    {
        var body = TerminalQr.Render(Link, colour: false).Replace("\r", "").Replace("\n", "");

        Assert.All(body, c => Assert.Contains(c, "█▀▄ "));
    }

    /// <summary>
    /// The corners are quiet zone, so they must be light — and "light" on a dark terminal is a
    /// printed block, not a space. Getting this backwards produces a code that looks perfect and
    /// scans on nothing.
    /// </summary>
    [Fact]
    public void TheQuietZoneIsLight()
    {
        var first = TerminalQr.Render(Link, colour: false).Split('\n')[0];

        Assert.Equal(new string('█', 85), first.TrimEnd('\r'));
    }

    [Fact]
    public void ColourNamesBothHalvesOfEveryCellAndResetsEachLine()
    {
        var line = TerminalQr.Render(Link).Split('\n')[0];

        // Foreground for the upper module, background for the lower, one per cell.
        Assert.Equal(85, line.Split("[38;2;").Length - 1);
        Assert.Equal(85, line.Split("[48;2;").Length - 1);

        // Left set, the rest of the row keeps the code's colours to the end of the line.
        Assert.EndsWith("[0m\r", line);
    }

    [Fact]
    public void ThereIsNothingToRenderForNothing()
    {
        Assert.Throws<ArgumentException>(() => TerminalQr.Render("  "));
    }

    /// <summary>
    /// Every module the terminal draws is the module QRCoder put in the symbol, in the right place
    /// and the right way round.
    ///
    /// <para>The shape of the output is easy to get right while the content is wrong: pack the two
    /// half-block rows the wrong way up, or invert light and dark, and the result is still 85 cells
    /// across and 43 lines down, still looks exactly like a QR code, and scans on nothing. This
    /// reads the grid back out of the rendered characters and compares it against the symbol.</para>
    /// </summary>
    [Fact]
    public void TheDrawnGridIsTheSymbolPlusItsQuietZone()
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(Link, QRCodeGenerator.ECCLevel.L);
        var symbol = data.ModuleMatrix.Count;

        var lines = TerminalQr.Render(Link, colour: false)
            .ReplaceLineEndings("\n")
            .TrimEnd('\n')
            .Split('\n');

        for (var y = 0; y < symbol; y++)
        {
            for (var x = 0; x < symbol; x++)
            {
                // Four modules of quiet zone offset the symbol inside the drawn grid.
                var drawnRow = y + 4;
                var cell = lines[drawnRow / 2][x + 4];

                // A block is a LIGHT module, so dark is the absence of ink in that half.
                var drawnDark = drawnRow % 2 == 0
                    ? cell is ' ' or '▄'   // upper half unlit
                    : cell is ' ' or '▀';  // lower half unlit

                Assert.Equal(data.ModuleMatrix[y][x], drawnDark);
            }
        }
    }
}
