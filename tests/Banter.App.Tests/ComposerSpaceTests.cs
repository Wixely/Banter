using Banter.App;
using CupriFace;
using CupriFace.Interaction;
using Xunit;

namespace Banter.App.Tests;

/// <summary>
/// A space typed into the composer has to appear when it is typed.
///
/// <para>Without <c>white-space: pre-wrap</c> the ordinary whitespace rules collapse a space at
/// the end of a line: it takes no width, the caret does not move, and nothing on screen changes
/// until the next character makes the space interior — at which point both appear at once. It
/// reads as dropped input. An HTML <c>&lt;textarea&gt;</c> avoids this by defaulting to
/// <c>pre-wrap</c>; <c>cupri-textarea</c> does not, so the page has to ask.</para>
///
/// <para>Asserted against rendered pixels rather than the model, because the model was never the
/// problem — <c>Model.Composer</c> held the space all along. Only the screen disagreed.</para>
/// </summary>
public sealed class ComposerSpaceTests
{
    private const int Width = 1000;
    private const int Height = 700;

    private static (BanterChatApp App, CupriDocument Doc) FocusedComposer()
    {
        var vm = new ChatViewModel();
        vm.SetNick("alice");
        vm.AddRoom("#main");
        vm.SwitchTo("#main");

        var app = new BanterChatApp(vm);
        var doc = app.CreateDocument();
        doc.BuildDisplayList(Width, Height);

        for (var y = (float)Height - 1; y > Height - 200; y -= 2)
        {
            for (var x = 40f; x < Width - 40; x += 8)
            {
                if (doc.HitTest(x, y)?.Element?.Closest("cupri-textarea") is null)
                {
                    continue;
                }

                doc.DispatchClick(x, y, 1);
                return (app, doc);
            }
        }

        throw new Xunit.Sdk.XunitException("nothing painted belongs to the composer");
    }

    private static byte[] Frame(BanterChatApp app, CupriDocument doc)
    {
        app.Present(Width, Height);
        doc.BuildDisplayList(Width, Height);
        return doc.RenderToPixels(Width, Height, SkiaSharp.SKColors.Black);
    }

    private static int Changed(byte[] a, byte[] b)
    {
        var n = 0;
        for (var i = 0; i < a.Length; i++)
        {
            if (a[i] != b[i])
            {
                n++;
            }
        }

        return n;
    }

    [Fact]
    public void TypingASpaceChangesTheScreen()
    {
        var (app, doc) = FocusedComposer();
        using var _ = doc;

        doc.DispatchKey("a", EditKey.None);
        var before = Frame(app, doc);

        doc.DispatchKey(" ", EditKey.None);
        var after = Frame(app, doc);

        // Zero is the bug: the caret has not moved and the space is invisible until the next
        // character arrives.
        Assert.True(
            Changed(before, after) > 0,
            "typing a space changed nothing on screen - the composer has lost white-space: pre-wrap");
    }

    [Fact]
    public void ARunOfSpacesKeepsMovingTheCaret()
    {
        // Every one of them, not just the first: indenting inside a message, or pausing between
        // words, must not look like a keyboard that has stopped responding.
        var (app, doc) = FocusedComposer();
        using var _ = doc;

        doc.DispatchKey("a", EditKey.None);
        var frame = Frame(app, doc);

        for (var i = 1; i <= 3; i++)
        {
            doc.DispatchKey(" ", EditKey.None);
            var next = Frame(app, doc);
            Assert.True(Changed(frame, next) > 0, $"space {i} changed nothing on screen");
            frame = next;
        }
    }

    [Fact]
    public void TheSpaceReachesTheModelEitherWay()
    {
        // The half that was never broken, kept so a future fix to the rendering cannot quietly
        // start eating the character instead.
        var vm = new ChatViewModel();
        vm.SetNick("alice");
        vm.AddRoom("#main");
        vm.SwitchTo("#main");

        var app = new BanterChatApp(vm);
        using var doc = app.CreateDocument();
        doc.BuildDisplayList(Width, Height);

        for (var y = (float)Height - 1; y > Height - 200; y -= 2)
        {
            var hit = false;
            for (var x = 40f; x < Width - 40; x += 8)
            {
                if (doc.HitTest(x, y)?.Element?.Closest("cupri-textarea") is null)
                {
                    continue;
                }

                doc.DispatchClick(x, y, 1);
                hit = true;
                break;
            }

            if (hit)
            {
                break;
            }
        }

        foreach (var ch in "a b")
        {
            doc.DispatchKey(ch.ToString(), EditKey.None);
        }

        app.Present(Width, Height);
        Assert.Equal("a b", vm.Model.Composer);
    }
}
