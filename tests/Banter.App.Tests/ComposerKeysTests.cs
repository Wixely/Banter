using Banter.App;
using CupriFace.Interaction;
using Xunit;

namespace Banter.App.Tests;

/// <summary>
/// What the keyboard does in the composer, driven through a real document.
///
/// <para>These are written as behaviour rather than as registrations on purpose. The send shortcut
/// was registered and dead for weeks — the engine's lookup was gated on the keystroke's text being
/// one character long, and Enter arrives with no text at all (CupriFace#88) — and the test that
/// covered it asserted the placeholder <i>said</i> Ctrl+Enter, so it passed the whole time. A test
/// that presses the key is the only kind that could have caught it.</para>
/// </summary>
public sealed class ComposerKeysTests
{
    private const int Width = 1100;
    private const int Height = 760;

    private static (BanterChatApp App, ChatViewModel Vm, List<(string Room, string Text)> Sent) Room()
    {
        var vm = new ChatViewModel();
        vm.SetNick("alice");
        vm.AddRoom("#main");

        var sent = new List<(string, string)>();
        var app = new BanterChatApp(vm)
        {
            SendAsync = (room, text) => { sent.Add((room, text)); return Task.CompletedTask; },
        };
        return (app, vm, sent);
    }

    /// <summary>Types into the composer the way a person does: click it, then press keys.</summary>
    private static CupriFace.CupriDocument Typing(BanterChatApp app, string text)
    {
        var doc = app.CreateDocument();
        doc.BuildDisplayList(Width, Height);

        for (var y = (float)Height - 1; y > Height - 140; y -= 2)
        {
            for (var x = 40f; x < Width - 40; x += 8)
            {
                if (doc.HitTest(x, y)?.Element?.Closest("cupri-textarea") is not null)
                {
                    doc.DispatchClick(x, y, 1);
                    foreach (var ch in text)
                    {
                        doc.DispatchKey(ch.ToString(), EditKey.None);
                    }

                    return doc;
                }
            }
        }

        throw new Xunit.Sdk.XunitException("nothing painted belongs to the composer");
    }

    [Fact]
    public void EnterSends()
    {
        var (app, vm, sent) = Room();
        using var doc = Typing(app, "hello");

        doc.DispatchKey("", EditKey.Enter);
        vm.ApplyPending();

        Assert.Equal(("#main", "hello"), Assert.Single(sent));
    }

    [Fact]
    public void ShiftEnterWritesANewlineInstead()
    {
        var (app, vm, sent) = Room();
        using var doc = Typing(app, "one");

        doc.DispatchKey("", EditKey.Enter, KeyMods.Shift);
        foreach (var ch in "two")
        {
            doc.DispatchKey(ch.ToString(), EditKey.None);
        }

        vm.ApplyPending();

        // Nothing left the room: a composer that sends on the key people use to start a second
        // paragraph makes a multi-line message impossible to write.
        Assert.Empty(sent);
        Assert.Equal("one\ntwo", vm.Model.Composer);
    }

    [Fact]
    public void SendingEmptiesTheComposerAndLeavesItReady()
    {
        var (app, vm, _) = Room();
        using var doc = Typing(app, "hello");

        doc.DispatchKey("", EditKey.Enter);
        vm.ApplyPending();

        Assert.Equal("", vm.Model.Composer);

        // The field still has focus, so the next sentence can simply be typed. The engine keeps it
        // across a submit; this is here because losing it would be silent and infuriating.
        foreach (var ch in "again")
        {
            doc.DispatchKey(ch.ToString(), EditKey.None);
        }

        vm.ApplyPending();
        Assert.Equal("again", vm.Model.Composer);
    }

    [Fact]
    public void AnEmptyComposerSendsNothing()
    {
        var (app, vm, sent) = Room();
        using var doc = Typing(app, "");

        doc.DispatchKey("", EditKey.Enter);
        vm.ApplyPending();

        // Enter on an empty line is how people clear their head, not how they post a blank message.
        Assert.Empty(sent);
    }

    [Fact]
    public void EscapeAbandonsAnEdit()
    {
        var (app, vm, sent) = Room();
        vm.Append("#main", "alice", "a typo", 0, id: "m1");

        using var doc = app.CreateDocument();
        doc.BuildDisplayList(Width, Height);

        // Put it in the state the right-click menu's "Edit" leaves behind. Getting there through
        // the menu is what ContextMenuTargetTests is for; what is under test here is the key that
        // gets back out.
        vm.Model.EditingId = "m1";
        vm.Model.Composer = "a typo";
        vm.Model.EditingClass = "editing-banner";

        doc.DispatchKey("", EditKey.Escape);
        vm.ApplyPending();

        Assert.Equal("", vm.Model.EditingId);
        Assert.Equal("", vm.Model.Composer);
        Assert.Contains("hidden", vm.Model.EditingClass);
        Assert.Empty(sent);
    }

    [Fact]
    public void TheHintSaysWhatTheKeysDo()
    {
        // The counterpart of the tests above: those prove the keys work, this proves the window
        // tells anyone. An undiscoverable shortcut is close to no shortcut.
        var app = new BanterChatApp(new ChatViewModel());

        Assert.Contains("Enter to send", app.Html, StringComparison.Ordinal);
        Assert.Contains("Shift+Enter", app.Html, StringComparison.Ordinal);
    }
}
