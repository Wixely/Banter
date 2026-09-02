using Banter.App;
using CupriFace;
using CupriFace.Interaction;
using Xunit;
using Xunit.Abstractions;

namespace Banter.App.Tests;

/// <summary>
/// Typing "@" offers the agents in the room, and taking one puts its exact name in the composer.
///
/// <para>Naming an agent reaches it directly whatever the room's dispatch mode says, so the name
/// has to come out right — a mention that misses by a character is not a mention, it is a sentence
/// the agent ignores. That is the whole reason this exists rather than leaving people to type it.
/// </para>
///
/// <para>Driven through a real document because the interesting failures are all in the wiring.
/// A bare <c>Enter</c> shortcut beats <c>submit-on-enter</c> outright, so binding accept-completion
/// that way would have left the composer unable to send at all — and nothing about the markup would
/// have looked wrong.</para>
/// </summary>
public sealed class MentionAutocompleteTests(ITestOutputHelper output)
{
    private const int Width = 1100;
    private const int Height = 760;

    private static (BanterChatApp App, ChatViewModel Vm, List<(string Room, string Text)> Sent) Room()
    {
        var vm = new ChatViewModel();
        vm.SetNick("alice");
        vm.AddRoom("#main");
        vm.SetAgents("#main",
        [
            ("scout", false, "search, summarise", false),
            ("scribe", true, "notes", false),
            ("dagger", true, "cli, tools", true),
        ]);

        var sent = new List<(string, string)>();
        var app = new BanterChatApp(vm)
        {
            SendAsync = (room, text) => { sent.Add((room, text)); return Task.CompletedTask; },
        };
        return (app, vm, sent);
    }

    /// <summary>Types into the composer the way a person does, and pumps the frame that shows it.</summary>
    private static CupriDocument Typing(BanterChatApp app, string text)
    {
        var doc = app.CreateDocument();
        doc.BuildDisplayList(Width, Height);

        for (var y = (float)Height - 1; y > Height - 160; y -= 2)
        {
            for (var x = 40f; x < Width - 40; x += 8)
            {
                if (doc.HitTest(x, y)?.Element?.Closest("cupri-textarea") is null)
                {
                    continue;
                }

                doc.DispatchClick(x, y, 1);
                foreach (var ch in text)
                {
                    doc.DispatchKey(ch.ToString(), EditKey.None);
                }

                // The list is rebuilt on the frame pump, because the engine writes typing into the
                // model rather than raising an event.
                app.Present(Width, Height);
                return doc;
            }
        }

        throw new Xunit.Sdk.XunitException("nothing painted belongs to the composer");
    }

    [Fact]
    public void TypingAnAtOffersTheAgentsInTheRoom()
    {
        var (app, vm, _) = Room();
        using var doc = Typing(app, "@s");

        output.WriteLine($"offered: {string.Join(", ", vm.Model.Mentions.Select(m => m.Nick))}");

        Assert.Equal(["scout", "scribe"], vm.Model.Mentions.Select(m => m.Nick));
        Assert.DoesNotContain("hidden", vm.Model.MentionsClass, StringComparison.Ordinal);

        // The first is highlighted, so Enter always has something to take.
        Assert.Contains("selected", vm.Model.Mentions[0].RowClass, StringComparison.Ordinal);
    }

    [Fact]
    public void EnterTakesTheHighlightedOneRatherThanSending()
    {
        var (app, vm, sent) = Room();
        using var doc = Typing(app, "@sc");

        doc.DispatchKey("", EditKey.Down, KeyMods.Ctrl);   // scout -> scribe
        doc.DispatchKey("", EditKey.Enter);
        vm.ApplyPending();

        Assert.Equal("@scribe ", vm.Model.Composer);
        Assert.Empty(sent);
        Assert.False(vm.MentionsOpen);
    }

    [Fact]
    public void TheSecondEnterSends()
    {
        var (app, vm, sent) = Room();
        using var doc = Typing(app, "@da");

        doc.DispatchKey("", EditKey.Enter);         // takes "dagger"
        foreach (var ch in "look at this")
        {
            doc.DispatchKey(ch.ToString(), EditKey.None);
        }

        app.Present(Width, Height);
        doc.DispatchKey("", EditKey.Enter);         // no list up, so this sends
        vm.ApplyPending();

        Assert.Equal(("#main", "@dagger look at this"), Assert.Single(sent));
    }

    [Fact]
    public void ClickingOneTakesIt()
    {
        var (app, vm, _) = Room();
        using var doc = Typing(app, "@scr");
        doc.BuildDisplayList(Width, Height);

        var hit = false;
        for (var y = 0f; y < Height && !hit; y += 2)
        {
            for (var x = 0f; x < Width; x += 8)
            {
                if (doc.HitTest(x, y)?.Element?.Closest("[data-mention]")?.GetAttribute("data-mention") != "scribe")
                {
                    continue;
                }

                doc.DispatchClick(x, y, 1);
                hit = true;
                break;
            }
        }

        Assert.True(hit, "the suggestion should be painted somewhere clickable");
        vm.ApplyPending();
        Assert.Equal("@scribe ", vm.Model.Composer);
    }

    [Fact]
    public void EscapePutsTheListAwayWithoutTouchingWhatWasTyped()
    {
        var (app, vm, sent) = Room();
        using var doc = Typing(app, "@sc");

        doc.DispatchKey("", EditKey.Escape);
        vm.ApplyPending();

        Assert.False(vm.MentionsOpen);
        Assert.Contains("hidden", vm.Model.MentionsClass, StringComparison.Ordinal);

        // What was typed survives — Escape dismissed the suggestion, not the sentence.
        Assert.Equal("@sc", vm.Model.Composer);
        Assert.Empty(sent);
    }

    [Fact]
    public void EscapeStillAbandonsAnEditWhenNoListIsUp()
    {
        var (app, vm, _) = Room();
        vm.Append("#main", "alice", "a typo", 0, id: "m1");

        using var doc = app.CreateDocument();
        doc.BuildDisplayList(Width, Height);

        vm.Model.EditingId = "m1";
        vm.Model.Composer = "a typo";
        vm.Model.EditingClass = "editing-banner";

        doc.DispatchKey("", EditKey.Escape);
        vm.ApplyPending();

        // The list takes Escape first, but only when it is actually up.
        Assert.Equal("", vm.Model.EditingId);
    }

    [Theory]
    [InlineData("@", true)]                          // just opened, offer everyone
    [InlineData("hey @sc", true)]                    // mid-sentence, at a word boundary
    [InlineData("@scout ", false)]                   // finished — a space ends the name
    [InlineData("bs-ppt@boylesports.com", false)]    // an address is not a mention
    [InlineData("nothing here", false)]
    public void TheListOnlyAppearsWhereAnAtActuallyOpensAName(string composer, bool offered)
    {
        // An "@" mid-word is part of a word. Offering agents inside an email address would put a
        // menu over the conversation every time somebody pasted one.
        Assert.Equal(offered, ChatViewModel.PartialMention(composer) is not null);
    }

    [Fact]
    public void AnAtThatMatchesNobodyOffersNothing()
    {
        var (app, vm, _) = Room();
        using var doc = Typing(app, "@zzz");

        Assert.False(vm.MentionsOpen);
        Assert.Contains("hidden", vm.Model.MentionsClass, StringComparison.Ordinal);
    }

    /// <summary>
    /// Ctrl, not a bare arrow. A focused field swallows plain Up and Down before a shortcut sees
    /// them, so the list cannot be walked with them today — the keystroke comes back handled and
    /// the binding never fires. Pinned so this test is what changes when that does.
    /// </summary>
    [Fact]
    public void APlainArrowNeverReachesTheList()
    {
        var (app, vm, _) = Room();
        using var doc = Typing(app, "@s");

        doc.DispatchKey("", EditKey.Down);

        Assert.Contains("selected", vm.Model.Mentions[0].RowClass, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSelectionWrapsAtBothEnds()
    {
        var (app, vm, _) = Room();
        using var doc = Typing(app, "@s");

        // Up from the first goes to the last, rather than sticking. A selection that stops moving
        // reads as a dead key.
        doc.DispatchKey("", EditKey.Up, KeyMods.Ctrl);
        Assert.Contains("selected", vm.Model.Mentions[^1].RowClass, StringComparison.Ordinal);

        doc.DispatchKey("", EditKey.Down, KeyMods.Ctrl);
        Assert.Contains("selected", vm.Model.Mentions[0].RowClass, StringComparison.Ordinal);
    }
}
