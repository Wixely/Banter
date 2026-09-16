using Banter.App;
using CupriFace;
using CupriFace.Dom;
using CupriFace.Interaction;
using CupriFace.Style;
using Xunit;
using Xunit.Abstractions;

namespace Banter.App.Tests;

/// <summary>
/// Everything you can tap is big enough to tap.
///
/// <para>Found by the CURSOR rather than by a list of class names. Anything the engine will show a
/// pointer over is something a finger will be aimed at, so a list maintained by hand would go stale
/// the first time a control was added — and the controls that go missing from such a list are the
/// rare ones, which are exactly the ones nobody notices are unusable.</para>
///
/// <para>Keyed on the INPUT, not the window. <c>cupri-coarse</c> is set by the host — the Android
/// and web hosts both declare <c>InputProfile.Touch</c> — so a touch laptop at 1920 gets the same
/// treatment as a phone, which a width breakpoint would have missed entirely.</para>
/// </summary>
public sealed class TouchTargetTests(ITestOutputHelper output)
{
    /// <summary>
    /// 44, not 48. Android's guidance is 48dp and Apple's is 44pt; 44 is the number both agree is
    /// enough, and the rail already has five buttons stacked in a column that a 48 would push past
    /// the height of a small phone in landscape.
    /// </summary>
    private const float MinTap = 44f;

    private static IEnumerable<(RenderNode Node, float Top)> Walk(RenderNode node, float parentTop = 0)
    {
        var top = parentTop + node.Y;
        yield return (node, top);
        foreach (var child in node.Children)
        {
            foreach (var d in Walk(child, top))
            {
                yield return d;
            }
        }
    }

    /// <summary>
    /// The outermost box under each pointer cursor. The property is INHERITED, so every span inside
    /// a button reports one too — and it is the button that gets tapped, not its label.
    /// </summary>
    private static IEnumerable<RenderNode> TapTargets(RenderNode root)
    {
        IEnumerable<RenderNode> Walk(RenderNode n, bool insideTarget)
        {
            // A pointer cursor, or a component that IS a button. cupri-button's own stylesheet
            // sets no cursor, so the cursor test alone misses Send, Attach, Save, Close and every
            // other control the engine draws for us — which is most of the ones a finger wants.
            var isTarget = !insideTarget
                           && (n.Style.Cursor == CursorType.Pointer
                               || string.Equals(n.Tag, "cupri-button", StringComparison.OrdinalIgnoreCase));
            if (isTarget)
            {
                yield return n;
            }

            foreach (var c in n.Children)
            {
                foreach (var d in Walk(c, insideTarget || isTarget))
                {
                    yield return d;
                }
            }
        }

        return Walk(root, false);
    }

    private static string Describe(RenderNode n) =>
        n.Element?.GetAttribute("class") is { Length: > 0 } cls ? cls : n.Tag;

    [Theory]
    [MemberData(nameof(AppPages.OnAnySize), MemberType = typeof(AppPages))]
    public void EveryTapTargetIsBigEnoughForAFinger(string page, int w, int h)
    {
        var app = AppPages.Showing(page);
        using var doc = app.CreateDocument();
        doc.InputProfile = InputProfile.Touch;   // what the Android and web hosts declare
        doc.Refresh();

        var p = BanterChatApp.Presentation(w, h);
        doc.BuildFrame(p.LogicalWidth, p.LogicalHeight);

        var small = new List<string>();
        foreach (var t in TapTargets(doc.Root))
        {
            if (t.Height <= 0 || t.Width <= 0)
            {
                continue;   // not on screen on this page; its own page's run covers it
            }

            if (t.Height + 0.5f < MinTap)
            {
                small.Add($"{Describe(t)} is {t.Width:F0}x{t.Height:F0}");
            }
        }

        foreach (var s in small.Distinct())
        {
            output.WriteLine(s);
        }

        Assert.True(small.Count == 0, $"{small.Distinct().Count()} tap target(s) under {MinTap}dp on '{page}' at {w}x{h}");
    }

    [Fact]
    public void TheDesktopCanAskForTheSizesAPhoneGets()
    {
        // A desktop window narrowed to a phone's width gets the phone LAYOUT, because those are
        // width rules — but not the phone SIZES, because those key off the engine's pointer class
        // and a desktop host always says "mouse". The preview was therefore systematically roomier
        // than the thing it previewed, on the axis where a phone has least to spare. This is the
        // setting that closes that, and the point of it is that it moves real pixels.
        var app = AppPages.Showing("chat");
        using var doc = app.CreateDocument();
        doc.Refresh();

        var p = BanterChatApp.Presentation(412, 915);
        doc.BuildFrame(p.LogicalWidth, p.LogicalHeight);

        // Height > 0: several rail buttons are `hidden` for a non-admin and stay in the tree at
        // no size, and the first one in document order is one of them.
        float RailButton() => TapTargets(doc.Root)
            .First(n => n.Element?.GetAttribute("class")?.Split(' ').Contains("rail-button") == true
                        && n.Height > 0)
            .Height;

        var withAMouse = RailButton();

        app.ViewModel.SetTouchLayout(true);
        doc.InputProfile = InputProfile.Touch;
        doc.Refresh();
        doc.BuildFrame(p.LogicalWidth, p.LogicalHeight);
        var withAFinger = RailButton();

        output.WriteLine($"rail button: {withAMouse:F0} with a mouse, {withAFinger:F0} with a finger");

        Assert.True(withAMouse < MinTap, "the mouse layout is already finger-sized, so this proves nothing");
        Assert.True(withAFinger >= MinTap, "asking for touch did not resize anything");
    }

    [Theory]
    [MemberData(nameof(AppPages.OnAnySize), MemberType = typeof(AppPages))]
    public void NoTapTargetIsPushedOffTheEdgeByItsOwnSize(string page, int w, int h)
    {
        // The other half of making things bigger, and the half that bites: a control grown to 44
        // can push the one beside it off the side, and a button off the edge is worse than a small
        // one — it is not there at all.
        //
        // This is CupriDoctor's CF0072 in miniature, and it is here rather than in PhoneFitTests
        // because CupriDoctor.Check builds its own document and takes no InputProfile, so it
        // cannot be asked about the coarse layout at all. Tap targets only, which is both the
        // part that matters and the part this can judge without re-implementing the engine's
        // clipping rules.
        var app = AppPages.Showing(page);
        using var doc = app.CreateDocument();
        doc.InputProfile = InputProfile.Touch;
        doc.Refresh();

        var p = BanterChatApp.Presentation(w, h);
        doc.BuildFrame(p.LogicalWidth, p.LogicalHeight);

        var offEdge = new List<string>();
        foreach (var (node, _) in Walk(doc.Root))
        {
            if (node.Width <= 0 || node.Height <= 0)
            {
                continue;
            }

            if (node.Style.Cursor != CursorType.Pointer
                && !string.Equals(node.Tag, "cupri-button", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var right = Left(doc.Root, node) + node.Width;
            if (right > doc.ViewportWidth + 0.5f)
            {
                offEdge.Add($"{Describe(node)} ends at {right:F0} of {doc.ViewportWidth:F0}");
            }
        }

        foreach (var s in offEdge.Distinct())
        {
            output.WriteLine(s);
        }

        Assert.True(offEdge.Count == 0, $"{offEdge.Distinct().Count()} tap target(s) off the edge on '{page}' at {w}x{h}");
    }

    /// <summary>Absolute left of a node, by finding it again on the way down.</summary>
    private static float Left(RenderNode root, RenderNode target)
    {
        float? Find(RenderNode n, float x)
        {
            var left = x + n.X;
            if (ReferenceEquals(n, target))
            {
                return left;
            }

            foreach (var c in n.Children)
            {
                if (Find(c, left) is { } found)
                {
                    return found;
                }
            }

            return null;
        }

        return Find(root, 0f) ?? 0f;
    }
}
