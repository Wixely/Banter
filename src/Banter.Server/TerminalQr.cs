using System.Text;
using QRCoder;

namespace Banter.Server;

/// <summary>
/// A CupriNet link as a QR code a phone can read off the terminal.
///
/// <para>A signed link is about 380 characters of base64. Nobody types that, and on a phone nobody
/// pastes it either — this exists because getting one onto a phone by hand is the single most
/// tedious step in standing a server up.</para>
/// </summary>
public static class TerminalQr
{
    /// <summary>
    /// The quiet zone the spec asks for, in modules. It is not decoration: a scanner uses it to
    /// find the code's edges, and a QR printed flush against other text is often simply not seen.
    /// </summary>
    private const int QuietZone = 4;

    /// <summary>
    /// Renders <paramref name="text"/> as lines of half-block characters, two module rows per
    /// line, so the modules come out roughly square in a terminal where a character cell is not.
    ///
    /// <para><b>Error correction is L on purpose</b>, which is the opposite of the usual advice.
    /// A link is long enough that the level decides whether this fits on a screen at all: L gives
    /// 77 modules across and M gives 85, and with the quiet zone that is the difference between 85
    /// columns and 93. Nothing here is printed on paper or read at an angle in bad light — it is
    /// on a screen a foot from the camera, which is the easiest job a scanner ever gets.</para>
    ///
    /// <para><b>Colour, when asked for, is what makes it scannable rather than what makes it
    /// pretty.</b> A QR must be dark modules on a light field; a terminal that renders text
    /// light-on-dark inverts that, and some scanners refuse an inverted code. Naming both colours
    /// explicitly per cell takes the terminal's theme out of it. Without colour the code is drawn
    /// for a dark background, because that is what a terminal usually is.</para>
    /// </summary>
    public static string Render(string text, bool colour = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        var dark = Modules(text);
        var size = dark.GetLength(0);
        var line = new StringBuilder();
        var output = new StringBuilder();

        // Two module rows per line. An odd-sized code leaves the bottom half of the last line
        // empty, which is quiet zone anyway.
        for (var y = 0; y < size; y += 2)
        {
            line.Clear();
            for (var x = 0; x < size; x++)
            {
                var upper = dark[y, x];
                var lower = y + 1 < size && dark[y + 1, x];

                if (colour)
                {
                    // The half block paints the upper half in the foreground colour and leaves the
                    // lower half to the background, so one cell carries two modules.
                    line.Append(upper ? "[38;2;0;0;0m" : "[38;2;255;255;255m");
                    line.Append(lower ? "[48;2;0;0;0m" : "[48;2;255;255;255m");
                    line.Append('▀');
                }
                else
                {
                    // Drawn for a dark terminal: a block is a LIGHT module, so the dark modules are
                    // the background showing through and the polarity is the right way round.
                    line.Append((upper, lower) switch
                    {
                        (false, false) => '█',   // both light
                        (false, true) => '▀',    // upper light
                        (true, false) => '▄',    // lower light
                        (true, true) => ' ',          // both dark
                    });
                }
            }

            if (colour)
            {
                line.Append("[0m");
            }

            output.AppendLine(line.ToString());
        }

        return output.ToString();
    }

    /// <summary>How many columns <see cref="Render"/> will need, so a caller can say so before
    /// printing something wider than the window and shredding it.</summary>
    public static int Columns(string text) => Modules(text).GetLength(0);

    /// <summary>
    /// The code as a grid of dark/light, quiet zone included.
    ///
    /// <para>QRCoder's <c>ModuleMatrix</c> is the bare symbol — 77 rows for the version-15 code a
    /// link produces, not 85 — so the quiet zone is added here rather than assumed.</para>
    /// </summary>
    private static bool[,] Modules(string text)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.L);

        var symbol = data.ModuleMatrix.Count;
        var size = symbol + (QuietZone * 2);
        var dark = new bool[size, size];

        for (var y = 0; y < symbol; y++)
        {
            var row = data.ModuleMatrix[y];
            for (var x = 0; x < symbol; x++)
            {
                dark[y + QuietZone, x + QuietZone] = row[x];
            }
        }

        return dark;
    }
}
