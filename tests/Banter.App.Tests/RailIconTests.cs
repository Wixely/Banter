using Banter.App;
using CupriFace;
using Xunit;
using Xunit.Abstractions;

namespace Banter.App.Tests;

/// <summary>
/// The rail's icons. They are drawn from boxes rather than set as glyphs — Skia falls back to
/// whatever font the host resolves and no face is embedded, so a glyph would render differently
/// on desktop, Android and the web, or show tofu.
///
/// <para>The cost of drawing them is that a mistyped rule fails silently: the element still
/// exists, still lays out, and paints nothing. These tests hit-test the actual painted output for
/// every part of every icon, which is the only way that shows up before someone looks at it.</para>
/// </summary>
public sealed class RailIconTests(ITestOutputHelper output)
{
    private const int Width = 1240;
    private const int Height = 800;

    private static ChatViewModel Room()
    {
        var vm = new ChatViewModel();
        vm.SetNick("alice");
        vm.AddRoom("#main");
        vm.SetIsAdmin(true);
        vm.SetToolCatalogue([("read", "files", "read a file")]);
        return vm;
    }

    [Theory]
    // Every part of every icon, by the class that draws it. A part missing from the paint is a
    // rule that did not apply — a typo, or a property the engine does not support.
    [InlineData(".chat-body")]
    [InlineData(".chat-tail")]
    [InlineData(".tools-track-top")]
    [InlineData(".tools-track-bottom")]
    [InlineData(".tools-knob-top")]
    [InlineData(".tools-knob-bottom")]
    [InlineData(".net-hub")]
    [InlineData(".net-top")]
    [InlineData(".net-bottom-left")]
    [InlineData(".net-bottom-right")]
    [InlineData(".net-edge-up")]
    [InlineData(".net-edge-left")]
    [InlineData(".net-edge-right")]
    [InlineData(".user-head")]
    [InlineData(".user-body")]
    public void EveryIconPartActuallyPaints(string selector)
    {
        using var doc = new BanterChatApp(Room()).CreateDocument();
        doc.BuildDisplayList(Width, Height);

        var hits = 0;
        for (var y = 0f; y < 300; y += 1)
        {
            for (var x = 0f; x < 76; x += 1)
            {
                if (doc.HitTest(x, y)?.Element?.Closest(selector) is not null)
                {
                    hits++;
                }
            }
        }

        output.WriteLine($"{selector}: {hits} px");
        Assert.True(hits > 0, $"{selector} painted nothing — its rule did not apply.");
    }

    [Fact]
    public void TheRailCarriesNoLettersWhereIconsBelong()
    {
        // The rail used to be "B", "T", "A". A letter that crept back would be a regression that
        // every other test here would happily pass.
        var html = new BanterChatApp(Room()).Html;
        var start = html.IndexOf("<div class=\"rail\">", StringComparison.Ordinal);
        var rail = html[start..html.IndexOf("class=\"sidebar\"", StringComparison.Ordinal)];
        output.WriteLine(rail);

        // Any element whose entire content is a single letter is a leftover placeholder.
        Assert.DoesNotMatch(new System.Text.RegularExpressions.Regex(@">\s*[A-Za-z]\s*<"), rail);
    }
}
