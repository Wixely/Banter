using Banter.App;
using CupriFace;
using CupriFace.Dom;
using Xunit;
using Xunit.Abstractions;

namespace Banter.App.Tests;

/// <summary>
/// The composer is one control, not a box inside a box.
///
/// <para>It was two: a rounded bordered row, and inside it a field drawing its own hard-edged
/// 2px rectangle, tight around the text and lit pink on focus. That reads as a small input
/// dropped into a bigger one. The row is now the only box, and the ring moved to it.</para>
/// </summary>
public sealed class ComposerBoxTests(ITestOutputHelper output)
{
    private static IEnumerable<RenderNode> Walk(RenderNode n)
    {
        yield return n;
        foreach (var c in n.Children)
        {
            foreach (var d in Walk(c))
            {
                yield return d;
            }
        }
    }

    private static ChatViewModel Room()
    {
        var vm = new ChatViewModel();
        vm.SetNick("alice");
        vm.AddRoom("#main");
        vm.SwitchTo("#main");
        vm.Connected("tcp://h:7770", "alice");
        return vm;
    }

    /// <summary>
    /// The field inside draws nothing: no border, no background of its own. Measured from the
    /// computed style, because that is what the painter reads - a border is drawn whenever one is
    /// declared, whatever colour it is.
    /// </summary>
    [Fact]
    public void OnlyTheRowDrawsABox()
    {
        var vm = Room();
        var app = new BanterChatApp(vm);
        using var doc = app.CreateDocument();
        doc.Refresh();
        var p = BanterChatApp.Presentation(412, 915);
        doc.BuildFrame(p.LogicalWidth, p.LogicalHeight);

        var field = Walk(doc.Root).First(n =>
            n.Element?.GetAttribute("class")?.Split(' ').Contains("composer") == true);
        var row = Walk(doc.Root).First(n =>
            n.Element?.GetAttribute("class")?.Split(' ').Contains("composer-row") == true);

        output.WriteLine($"field borders: {field.BorderLeftW}/{field.BorderTopW}, "
            + $"row borders: {row.BorderLeftW}/{row.BorderTopW}");

        Assert.Equal(0f, field.BorderLeftW, 1);
        Assert.Equal(0f, field.BorderTopW, 1);
        Assert.True(row.BorderLeftW > 0f, "the row should be the one with a border");
    }

    /// <summary>
    /// Focus lights the row, and only the colour changes - a ring that also changed the width
    /// would move everything above the composer by a pixel as the caret arrived.
    /// </summary>
    [Fact]
    public void FocusLightsTheRowWithoutResizingIt()
    {
        var vm = Room();
        var app = new BanterChatApp(vm);
        using var doc = app.CreateDocument();
        doc.Refresh();
        var p = BanterChatApp.Presentation(1280, 800);
        doc.BuildFrame(p.LogicalWidth, p.LogicalHeight);

        var before = Walk(doc.Root)
            .First(n => n.Element?.GetAttribute("class")?.Split(' ').Contains("composer-row") == true);
        var (width, height) = (before.Width, before.Height);

        Assert.True(vm.SetComposerFocused(true));
        Assert.Contains("focused", vm.Model.ComposerRowClass, StringComparison.Ordinal);

        doc.Refresh();
        doc.BuildFrame(p.LogicalWidth, p.LogicalHeight);
        var after = Walk(doc.Root)
            .First(n => n.Element?.GetAttribute("class")?.Split(' ').Contains("composer-row") == true);

        output.WriteLine($"{width:F0}x{height:F0} -> {after.Width:F0}x{after.Height:F0}");
        Assert.Equal(width, after.Width, 1);
        Assert.Equal(height, after.Height, 1);
    }

    /// <summary>Said once per change, not once per keystroke: the engine reports text-input state
    /// on every character, and a frame each to redraw a border that has not moved is a frame
    /// wasted per key.</summary>
    [Fact]
    public void TheSameFocusIsNotRepaintedOverAndOver()
    {
        var vm = Room();

        Assert.True(vm.SetComposerFocused(true));
        Assert.False(vm.SetComposerFocused(true));
        Assert.True(vm.SetComposerFocused(false));
        Assert.False(vm.SetComposerFocused(false));
    }

    /// <summary>
    /// The ring is hung on "a multiline field has focus", which is only honest while the composer
    /// is the only one. This is the guard on that: a second cupri-textarea anywhere would light
    /// the composer's box while somebody typed somewhere else entirely.
    /// </summary>
    [Fact]
    public void TheComposerIsTheOnlyMultilineField()
    {
        var vm = Room();
        var app = new BanterChatApp(vm);

        var areas = app.Html.Split("<cupri-textarea").Length - 1;
        output.WriteLine($"{areas} cupri-textarea element(s) in the markup");

        Assert.Equal(1, areas);
        Assert.Contains("class=\"composer\"", app.Html, StringComparison.Ordinal);
    }
}
