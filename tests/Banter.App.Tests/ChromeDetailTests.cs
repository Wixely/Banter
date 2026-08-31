using Banter.App;
using CupriFace.Dom;
using CupriFace.Style;
using Xunit;

namespace Banter.App.Tests;

/// <summary>
/// The small things that make a window look finished rather than half-wired: separators that only
/// appear between two things, badges that only appear when there is a count, and a pointer over
/// what can be clicked.
/// </summary>
public sealed class ChromeDetailTests
{
    private const int Width = 1100;
    private const int Height = 760;

    private static IEnumerable<(RenderNode Node, float Left, float Top)> Walk(
        RenderNode node, float parentLeft = 0, float parentTop = 0)
    {
        var left = parentLeft + node.X;
        var top = parentTop + node.Y;
        yield return (node, left, top);
        foreach (var child in node.Children)
        {
            foreach (var descendant in Walk(child, left, top))
            {
                yield return descendant;
            }
        }
    }

    [Fact]
    public void TheHeaderNeverLeadsOrTrailsWithASeparator()
    {
        var vm = new ChatViewModel();

        // Before any room exists, neither half is known and the header used to be a lone dot.
        Assert.Equal("", vm.Model.Dispatch);

        // A room with no dispatch mode set yet knows only who is dispatching, and the markup's
        // literal "{{DispatchMode}} · {{Delegator}}" put a dot in front of it.
        vm.AddRoom("#main");
        Assert.Equal("no delegator", vm.Model.Dispatch);
        Assert.DoesNotContain("·", vm.Model.Dispatch);
    }

    [Fact]
    public void TheSeparatorAppearsOnlyWhenBothHalvesDo()
    {
        var vm = new ChatViewModel();
        vm.AddRoom("#main");

        vm.SetDelegator("#main", "dagger");
        Assert.Equal("dagger", vm.Model.Dispatch);

        vm.SetDispatchMode("#main", "delegated");
        Assert.Equal("delegated · dagger", vm.Model.Dispatch);
    }

    [Fact]
    public void TheToolPanelIsJustCalledToolsUntilAnAgentIsChosen()
    {
        var vm = new ChatViewModel();
        vm.AddRoom("#main");

        Assert.Equal("Tools", vm.Model.ToolsTitle);

        vm.SelectToolAgent("dagger");
        Assert.Equal("Tools · dagger", vm.Model.ToolsTitle);
    }

    [Fact]
    public void ARoomWithNothingUnreadWearsNoBadge()
    {
        var vm = new ChatViewModel();
        vm.SetNick("alice");
        vm.AddRoom("#main");
        vm.AddRoom("#other");

        // An empty badge still paints its background and padding, so this was a blank blue pill
        // beside every quiet room.
        var quiet = vm.Model.Rooms.Single(r => r.Name == "#other");
        Assert.Contains("hidden", quiet.BadgeClass);

        vm.Append("#other", "bob", "something", 0);
        Assert.Equal("1", quiet.Badge);
        Assert.DoesNotContain("hidden", quiet.BadgeClass);

        vm.SwitchTo("#other");
        Assert.Equal("", quiet.Badge);
        Assert.Contains("hidden", quiet.BadgeClass);
    }

    [Fact]
    public void TheVoiceStripIsAbsentUntilAHeadWiresAudio()
    {
        var vm = new ChatViewModel();
        vm.AddRoom("#main");

        Assert.Contains("hidden", vm.Model.VoiceRowClass);

        vm.EnableVoice(readbackAvailable: true);
        Assert.DoesNotContain("hidden", vm.Model.VoiceRowClass);
    }

    [Fact]
    public void WhatCanBeClickedShowsAPointer()
    {
        var vm = new ChatViewModel();
        vm.SetNick("alice");
        vm.AddRoom("#main");
        vm.Append("#main", "bob", "hello", 0);

        var app = new BanterChatApp(vm);
        using var doc = app.CreateDocument();
        doc.BuildDisplayList(Width, Height);

        // The room tab in the sidebar: a thing that responds to a click should say so before it
        // is clicked.
        var tab = Walk(doc.Root).First(n => n.Node.Style?.Cursor == CursorType.Pointer);
        var cursor = doc.CursorAt(tab.Left + (tab.Node.Width / 2), tab.Top + (tab.Node.Height / 2));

        Assert.Equal(CursorType.Pointer, cursor);
    }

    [Fact]
    public void TheComposerDoesNotPromiseAShortcutThatCannotFire()
    {
        // This test used to assert the placeholder SAID "Ctrl+Enter to send", and passed for weeks
        // while the shortcut did nothing: OnShortcut only matches single-character text, and Enter
        // arrives as an EditKey with none (CupriFace#88). It pinned the promise rather than the
        // behaviour, which is the more useful thing to have learnt from it.
        //
        // Restore the hint when #88 lands — the binding is still registered and waiting.
        var app = new BanterChatApp(new ChatViewModel());

        Assert.DoesNotContain("Ctrl+Enter", app.Html);
    }

    [Fact]
    public void TheWindowChromeMatchesTheApp()
    {
        var app = new BanterChatApp(new ChatViewModel());

        // Windows gives a window light chrome unless asked otherwise, which on a dark app is a
        // white band bolted to the top of it.
        Assert.True(app.DarkWindowChrome);
        Assert.Equal(new SkiaSharp.SKColor(0x14, 0x16, 0x1a), app.Background);
    }

}
