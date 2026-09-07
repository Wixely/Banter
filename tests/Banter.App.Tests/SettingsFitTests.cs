using Banter.App;
using CupriFace;
using Xunit;

namespace Banter.App.Tests;

/// <summary>
/// Every settings control has to be reachable.
///
/// <para>Not a style point. The settings list declares <c>overflow: scroll</c> and does not
/// scroll — CupriFace 0.18.0 ignores the wheel on an overflow box, measured — so the card's height
/// is a hard ceiling, and anything past it cannot be reached at all. The settings had grown about
/// 340px beyond that before this was noticed, which is how a control ends up invisible to
/// everyone and obvious to no one.</para>
/// </summary>
public sealed class SettingsFitTests
{
    private const int Width = 1240;
    private const int Height = 800;

    private static (BanterChatApp App, ChatViewModel Vm) Ready()
    {
        var vm = new ChatViewModel();
        vm.SetNick("alice");
        vm.AddRoom("#main");
        vm.SwitchTo("#main");
        vm.Append("#main", "dagger", "hello", 0);
        vm.SetAutoSubmit(true, 0);
        vm.SetFlashOnMention(true);
        vm.SetVoiceSettings(
            "local", "en", "dagger", "http://localhost:1234", "localhost:10200",
            [("aiden", "Aiden"), ("bree", "Bree")],
            new Dictionary<string, string>());
        // Signed in, or the sign-out control is hidden and "reachable" would mean nothing.
        vm.Connected("tcp://host:7770", "alice");
        vm.ShowSettingsPanel(true);
        return (new BanterChatApp(vm), vm);
    }

    private static bool Painted(CupriDocument doc, string selector)
    {
        for (var y = 0f; y < Height; y += 3)
        {
            for (var x = 0f; x < Width; x += 3)
            {
                if (doc.HitTest(x, y)?.Element?.Closest(selector) is not null)
                {
                    return true;
                }
            }
        }

        return false;
    }

    [Theory]
    [InlineData("alerts", "[data-flash]")]
    [InlineData("you", "[data-zoom]")]
    [InlineData("you", ".sign-out")]
    [InlineData("listening", "[data-transcribe]")]
    [InlineData("transcripts", "[data-autosubmit]")]
    [InlineData("speaking", "[data-voice-cycle]")]
    public void EveryControlIsReachableInItsSection(string section, string selector)
    {
        var (app, vm) = Ready();
        vm.ShowSettingsSection(section);

        using var doc = app.CreateDocument();

        // Refresh, not Present: class bindings are re-read on a refresh, and these tests change
        // the model directly rather than posting, so Present sees nothing pending and lays the
        // page out from the state it had when the document was created.
        doc.Refresh();
        doc.BuildDisplayList(Width, Height);

        Assert.True(Painted(doc, selector), $"{selector} is not reachable in the '{section}' section");
    }

    [Fact]
    public void TheSectionsThemselvesAreAlwaysReachable()
    {
        // If the tabs fall off the card there is no way back to anything.
        var (app, _) = Ready();
        using var doc = app.CreateDocument();

        // Refresh, not Present: class bindings are re-read on a refresh, and these tests change
        // the model directly rather than posting, so Present sees nothing pending and lays the
        // page out from the state it had when the document was created.
        doc.Refresh();
        doc.BuildDisplayList(Width, Height);

        foreach (var section in new[] { "you", "alerts", "listening", "transcripts", "speaking" })
        {
            Assert.True(
                Painted(doc, $"[data-settings-section=\"{section}\"]"),
                $"the '{section}' tab is not reachable");
        }
    }

    [Fact]
    public void OnlyOneSectionShowsAtATime()
    {
        // The whole reason for sections: two at once is how the card overflowed in the first place.
        var (app, vm) = Ready();
        vm.ShowSettingsSection("listening");

        using var doc = app.CreateDocument();

        // Refresh, not Present: class bindings are re-read on a refresh, and these tests change
        // the model directly rather than posting, so Present sees nothing pending and lays the
        // page out from the state it had when the document was created.
        doc.Refresh();
        doc.BuildDisplayList(Width, Height);

        Assert.True(Painted(doc, "[data-transcribe]"));
        Assert.False(Painted(doc, "[data-zoom]"), "a control from another section is showing");
        Assert.False(Painted(doc, "[data-autosubmit]"), "a control from another section is showing");
    }

    [Fact]
    public void WithNothingToSpeakWithTheVoicesFieldStaysAwayEvenInItsSection()
    {
        // Two rules decide that field: the section, and whether this machine can speak at all.
        // Showing an empty picker reads as a list that failed to load.
        var vm = new ChatViewModel();
        vm.SetNick("alice");
        vm.AddRoom("#main");
        vm.SetVoiceSettings("local", "", "", "", "", [], new Dictionary<string, string>());
        vm.ShowSettingsPanel(true);
        vm.ShowSettingsSection("speaking");

        var app = new BanterChatApp(vm);
        using var doc = app.CreateDocument();

        // Refresh, not Present: class bindings are re-read on a refresh, and these tests change
        // the model directly rather than posting, so Present sees nothing pending and lays the
        // page out from the state it had when the document was created.
        doc.Refresh();
        doc.BuildDisplayList(Width, Height);

        Assert.False(Painted(doc, "[data-voice-cycle]"));
    }
}
