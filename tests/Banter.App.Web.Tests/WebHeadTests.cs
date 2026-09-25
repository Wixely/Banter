using Microsoft.Playwright;
using SkiaSharp;
using Xunit.Abstractions;

namespace Banter.App.Web.Tests;

/// <summary>
/// Does the published head actually run?
///
/// <para>Everything else in this repository tests the app below the seam: the view model, the
/// document, the client, the transports. None of it can fail the way a WebAssembly head fails.
/// The failures that live only here are a trimmed-away formatter, an AOT publish that paints
/// nothing, and a fingerprinted script whose name moved - and that last one is the shape worth
/// naming, because the page then loads to a blank canvas with NO error on it. The app never
/// started, so it cannot report that it did not.</para>
///
/// <para>The canvas is opaque to automation, but the head is not: CupriFace publishes the
/// document's ARIA tree into the DOM on every platform, web included. So the sign-in screen can
/// be found by role and name, the way a screen reader finds it, rather than by pixel.</para>
/// </summary>
[Collection(nameof(PublishedHead))]
public sealed class WebHeadTests(PublishedHead head, ITestOutputHelper output)
{
    /// <summary>Booting a WebAssembly runtime, decompressing ~12 MB of BCL and building the first
    /// frame is seconds on a good machine and longer on a cold CI runner. Generous on purpose:
    /// this bound exists to fail a hang, not to measure anything.</summary>
    private const int BootTimeoutMs = 90_000;

    /// <summary>The node's seed file. Absent unless a Nodestar was asked to leave one, which is
    /// the normal case for a plain deployment - the head polls for it and gives up.</summary>
    private const string ExpectedMiss = "seed.json";

    [Fact]
    public async Task ItBootsToItsSignInScreen()
    {
        var page = await head.OpenAsync();
        await page.GotoAsync(head.BaseUrl);

        var connect = page.GetByRole(AriaRole.Button, new() { Name = "Connect" });
        await connect.WaitForAsync(new() { Timeout = BootTimeoutMs });

        // The whole form, not just that something appeared: each field is named, which is both
        // the proof that the document built and the thing a screen reader needs.
        Assert.Equal("Banter", await page.TitleAsync());
        await page.GetByRole(AriaRole.Textbox, new() { Name = "your nick" }).WaitForAsync();
        await page.GetByRole(AriaRole.Textbox, new() { Name = "Password" }).WaitForAsync();
    }

    /// <summary>
    /// The document can build and the page still be blank - Skia draws through WebGL, which is a
    /// different thing from the runtime starting. One flat colour means nothing was painted.
    /// </summary>
    [Fact]
    public async Task ItPaintsSomething()
    {
        var page = await head.OpenAsync();
        await page.GotoAsync(head.BaseUrl);
        await page.GetByRole(AriaRole.Button, new() { Name = "Connect" })
            .WaitForAsync(new() { Timeout = BootTimeoutMs });

        using var shot = SKBitmap.Decode(await page.ScreenshotAsync());
        Assert.NotNull(shot);

        var colours = new HashSet<uint>();
        for (var y = 0; y < shot.Height; y += 4)
        {
            for (var x = 0; x < shot.Width; x += 4)
            {
                colours.Add((uint)shot.GetPixel(x, y));
            }
        }

        output.WriteLine($"{colours.Count} distinct colours in {shot.Width}x{shot.Height}");
        Assert.True(colours.Count > 8, $"the canvas is {colours.Count} colours - nothing was drawn");
    }

    /// <summary>
    /// Nothing the page asked for came back missing. This is the fingerprinted-asset failure:
    /// the head loads its runtime through an import map of hashed filenames, and one stale name
    /// is a 404 that stops the app before it can say anything.
    /// </summary>
    [Fact]
    public async Task EverythingItAsksForIsThere()
    {
        var page = await head.OpenAsync();

        var missing = new List<string>();
        page.Response += (_, response) =>
        {
            if (response.Status >= 400 && !response.Url.Contains(ExpectedMiss, StringComparison.Ordinal))
            {
                lock (missing)
                {
                    missing.Add($"{response.Status} {response.Url}");
                }
            }
        };

        await page.GotoAsync(head.BaseUrl);
        await page.GetByRole(AriaRole.Button, new() { Name = "Connect" })
            .WaitForAsync(new() { Timeout = BootTimeoutMs });

        lock (missing)
        {
            Assert.True(missing.Count == 0, string.Join("\n", missing));
        }
    }
}
