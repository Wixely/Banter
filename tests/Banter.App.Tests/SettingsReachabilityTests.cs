using Banter.App;
using CupriFace;
using CupriFace.Dom;
using CupriFace.Interaction;
using Xunit;
using Xunit.Abstractions;

namespace Banter.App.Tests;

/// <summary>
/// Every setting can be reached, whatever the window.
///
/// <para>This page carries a scar: the fields box declares <c>overflow: scroll</c>, and for a long
/// time that did nothing, so the card's height was a hard ceiling on how many settings could exist
/// and two of them were pushed somewhere nothing could reach. The sections exist because of it. The
/// engine scrolls the box now (<see cref="EngineConstraintTests"/>), and this is what keeps that
/// true for the page itself rather than for a box in isolation.</para>
/// </summary>
public sealed class SettingsReachabilityTests(ITestOutputHelper output)
{
    private static RenderNode? Find(RenderNode node, string cls)
    {
        if (node.Element?.GetAttribute("class") is { } c
            && c.Split(' ').Contains(cls, StringComparer.Ordinal))
        {
            return node;
        }

        foreach (var child in node.Children)
        {
            if (Find(child, cls) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>
    /// A phone is where the fields certainly outgrow the space: the card becomes the whole screen,
    /// and the screen is shorter than the fields are tall.
    /// </summary>
    [Theory]
    [InlineData(412, 915)]
    [InlineData(915, 412)]
    public void WhateverDoesNotFitCanBeScrolledTo(int w, int h)
    {
        var app = AppPages.Showing("settings");
        using var doc = app.CreateDocument();
        doc.InputProfile = InputProfile.Touch;
        doc.Refresh();

        var p = BanterChatApp.Presentation(w, h);
        doc.BuildFrame(p.LogicalWidth, p.LogicalHeight);

        var fields = Find(doc.Root, "mgmt-fields");
        Assert.NotNull(fields);
        output.WriteLine(
            $"{w}x{h}: fields {fields.Width:F0}x{fields.Height:F0}, content {fields.ScrollContentHeight:F0}, "
            + $"scrollable={fields.IsScrollable}");

        if (!fields.IsScrollable)
        {
            // Everything fits, which is the other way for a setting to be reachable.
            return;
        }

        // The wheel has to move it, or "scrollable" is a number nobody can act on. Sent inside the
        // box rather than at the window's centre, which on a narrow screen is a different column -
        // and at its SCREEN box, because a node's own X/Y are relative to its parent and a wheel
        // takes viewport coordinates. (They happen to agree when the card fills the screen, which
        // is exactly the kind of agreement that stops being true later.)
        var box = HitTesting.ScreenBox(fields);
        var handled = doc.DispatchWheel(box.X + (box.W / 2), box.Y + (box.H / 2), 120);
        doc.BuildFrame(p.LogicalWidth, p.LogicalHeight);

        Assert.True(handled, "a wheel inside the settings fields should scroll them");
        Assert.True(Find(doc.Root, "mgmt-fields")!.ScrollY > 0.5f, "and the box should have moved");
    }

    /// <summary>
    /// A short window is the other way to lose a setting. The card is centred in the viewport, so
    /// a card taller than the viewport loses its head and its foot at once - the Close button at
    /// one end and the last field at the other, both off-screen with nothing to scroll, which is
    /// what 1280x520 did before the card was given a ceiling.
    ///
    /// <para>Every window here is wide enough that the phone rules do not apply - this is the
    /// desktop card - and above the design width Adaptive clamps at scale 1, so the logical
    /// viewport is the window's own short height. 684 is what the card asks for (620 and 32 of
    /// margin a side), so the sizes step either side of it.</para>
    /// </summary>
    [Theory]
    [InlineData(1600, 525)]       // short and wide: the letterbox the width rules do not catch
    [InlineData(1280, 520)]       // what used to hang 51px off each end
    [InlineData(1280, 680)]       // a few px short of what the card wants
    [InlineData(1280, 700)]       // a few px more than it wants
    [InlineData(1280, 1050)]      // the ordinary desktop window
    public void TheCardNeverGrowsPastTheWindow(int w, int h)
    {
        var app = AppPages.Showing("settings");
        using var doc = app.CreateDocument();
        doc.Refresh();

        var p = BanterChatApp.Presentation(w, h);
        doc.BuildFrame(p.LogicalWidth, p.LogicalHeight);

        // Absolute boxes throughout: a node's X/Y are relative to its parent's border box, so a
        // card at y=214 holding a fields box that reports y=107 is not a contradiction, and
        // comparing the two raw numbers to each other or to the viewport proves nothing.
        var card = HitTesting.AbsoluteBox(Find(doc.Root, "settings-card")!);
        var fieldsNode = Find(doc.Root, "mgmt-fields");
        Assert.NotNull(fieldsNode);
        var fields = HitTesting.AbsoluteBox(fieldsNode);
        output.WriteLine(
            $"{w}x{h} (logical {p.LogicalWidth:F0}x{p.LogicalHeight:F0}): card y={card.Y:F0} "
            + $"h={card.H:F0}; fields y={fields.Y:F0} h={fields.H:F0} "
            + $"content={fieldsNode.ScrollContentHeight:F0} scrollable={fieldsNode.IsScrollable}");

        Assert.True(card.Y >= -0.5f, $"the card starts {-card.Y:F0}px above the window");
        Assert.True(
            card.Y + card.H <= p.LogicalHeight + 0.5f,
            $"the card ends {card.Y + card.H - p.LogicalHeight:F0}px past the window");

        // The card fitting is not the claim; the fields fitting INSIDE it is. A box whose height is
        // clamped after its children are laid out gives a card inside the window and fields
        // outside it, which is how `max-height` on this card failed while looking like it worked.
        Assert.True(
            fields.Y + fields.H <= card.Y + card.H + 0.5f,
            $"the fields end {fields.Y + fields.H - (card.Y + card.H):F0}px past the card");

        // And whatever does not fit in that box has to be scrollable, or it is simply gone.
        Assert.True(
            fieldsNode.IsScrollable || fieldsNode.ScrollContentHeight <= fields.H + 0.5f,
            $"{fieldsNode.ScrollContentHeight:F0} of fields in {fields.H:F0} that will not scroll");
    }
}
