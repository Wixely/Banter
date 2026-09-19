using Banter.App;
using QRCoder;
using Xunit;

namespace Banter.App.Tests;

/// <summary>
/// Reading a server link out of a camera frame.
///
/// <para>The camera is not here, and does not need to be: what arrives from one is a luminance
/// plane, so a frame can be built in a test and the half that can be got wrong — the decode, and
/// the decision about what is worth stopping on — is tested without a lens.</para>
/// </summary>
public sealed class QrScanTests
{
    private const string Link =
        "cuprinet://intone/ygEBBmJhbnRlciCH_HEKyRbYMQiOGMlFTDxFJoZMbLzNWyFJCVsRWLu4jwEACTEyNy4wLjAu";

    /// <summary>
    /// A frame of what a camera would see: the code in white space, scaled up the way it is when
    /// a phone is held in front of a screen.
    /// </summary>
    private static byte[] Frame(string text, out int width, out int height, int scale = 6, int quiet = 4)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.L);
        var symbol = data.ModuleMatrix.Count;

        var size = (symbol + (quiet * 2)) * scale;
        width = height = size;

        var pixels = new byte[size * size];
        Array.Fill(pixels, (byte)255);

        for (var y = 0; y < symbol; y++)
        {
            for (var x = 0; x < symbol; x++)
            {
                if (!data.ModuleMatrix[y][x])
                {
                    continue;
                }

                for (var dy = 0; dy < scale; dy++)
                {
                    for (var dx = 0; dx < scale; dx++)
                    {
                        pixels[(((y + quiet) * scale) + dy) * size + ((x + quiet) * scale) + dx] = 0;
                    }
                }
            }
        }

        return pixels;
    }

    [Fact]
    public void ALinkComesBackOutOfTheFrameItWentInto()
    {
        var pixels = Frame(Link, out var width, out var height);

        Assert.Equal(Link, QrScan.Read(pixels, width, height));
    }

    [Fact]
    public void MostFramesHaveNoCodeInThemAndThatIsNotAFailure()
    {
        // Flat grey is what a camera sees while somebody is still aiming it.
        var pixels = new byte[320 * 240];
        Array.Fill(pixels, (byte)128);

        Assert.Null(QrScan.Read(pixels, 320, 240));
    }

    [Fact]
    public void AFrameSmallerThanItClaimsIsNotRead()
    {
        Assert.Null(QrScan.Read(new byte[10], 320, 240));
        Assert.Null(QrScan.Read(new byte[100], 0, 0));
    }

    /// <summary>
    /// A phone is pointed at the world and the world is full of QR codes. Filling the Server field
    /// with a parcel label because it was the first code in shot would look like it had worked,
    /// which is worse than not scanning at all.
    /// </summary>
    [Theory]
    [InlineData("tcp://10.0.2.2:7770", true)]
    [InlineData("cuprinet://intone/abc", true)]
    [InlineData("cupri://network/banter", true)]
    [InlineData("https://example.com/parcel/12345", false)]
    [InlineData("WIFI:S:CafeWifi;T:WPA;P:hunter2;;", false)]
    [InlineData("just some text", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void OnlySomethingTheServerFieldCouldUseStopsTheScan(string? text, bool usable)
    {
        Assert.Equal(usable, QrScan.LooksLikeAServer(text));
    }

    [Fact]
    public void SurroundingWhitespaceIsNotWhatMakesALinkUnusable()
    {
        Assert.True(QrScan.LooksLikeAServer("  tcp://10.0.2.2:7770  "));
    }
}
