using Banter.App;
using CupriFace;
using Xunit;
using Xunit.Abstractions;

namespace Banter.App.Tests;

/// <summary>
/// The voice section of the settings page: what this machine hears with, speaks with, and who
/// sounds like what.
///
/// <para>All of it is a client setting on purpose — the voice pool belongs to whatever speech
/// server this machine talks to, and two people in the same room can reasonably want the same
/// agent to sound different. Nothing here crosses the wire, and these tests need no audio device.</para>
/// </summary>
public sealed class VoiceSettingsTests(ITestOutputHelper output)
{
    private const int Width = 1240;
    private const int Height = 800;

    private static readonly (string Id, string Label)[] Pool =
        [("aiden", "Aiden"), ("serena", "Serena"), ("cori", "Cori")];

    private static ChatViewModel Room(IReadOnlyList<(string, string)>? pool = null)
    {
        var vm = new ChatViewModel();
        vm.SetNick("alice");
        vm.AddRoom("#main");
        vm.SetVoiceSettings("local", "", "", "", "", pool ?? Pool,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        return vm;
    }

    [Fact]
    public void EveryoneHeardFromCanBeGivenAVoice()
    {
        var vm = Room();
        vm.Append("#main", "dagger", "on it", 0);
        vm.Append("#main", "bob", "thanks", 0);
        vm.Append("#main", "alice", "mine", 0);
        vm.System("#main", "something happened");

        // Whoever has spoken, and nobody else: not yourself, and not the system line, which has
        // no speaker to give a voice to.
        var listed = vm.Model.SpeakerVoices.Select(r => r.Nick).ToList();
        output.WriteLine(string.Join(", ", listed));
        Assert.Equal(["bob", "dagger"], listed);
    }

    [Fact]
    public void ASpeakerStepsThroughThePoolAndBackToAutomatic()
    {
        var vm = Room();
        vm.Append("#main", "dagger", "hello", 0);

        // Unpinned to the first voice, along the pool, then back to unpinned — so "let it choose"
        // is reachable by carrying on rather than by hunting for a clear.
        Assert.Equal("aiden", vm.CycleVoice("dagger"));
        vm.PinVoice("dagger", "aiden");
        Assert.Equal("serena", vm.CycleVoice("dagger"));
        vm.PinVoice("dagger", "serena");
        Assert.Equal("cori", vm.CycleVoice("dagger"));
        vm.PinVoice("dagger", "cori");
        Assert.Null(vm.CycleVoice("dagger"));

        vm.PinVoice("dagger", null);
        Assert.Empty(vm.VoicePins);
    }

    [Fact]
    public void ARowSaysWhichVoiceAndWhetherAnyoneChoseIt()
    {
        var vm = Room();
        vm.Append("#main", "dagger", "hello", 0);

        var dealt = vm.Model.SpeakerVoices.Single();
        Assert.Equal("dealt by name", dealt.VoiceLabel);
        Assert.Equal("automatic", dealt.VoiceKind);

        vm.PinVoice("dagger", "serena");
        var chosen = vm.Model.SpeakerVoices.Single();

        // The label, not the id: "serena" is what the server calls it, "Serena" is what a person
        // reads. And said in words, because which state a row is in is the content of the row.
        Assert.Equal("Serena", chosen.VoiceLabel);
        Assert.Equal("chosen", chosen.VoiceKind);
    }

    [Fact]
    public void NothingThatSpeaksMeansNoVoicePickerAtAll()
    {
        var vm = Room(pool: []);
        vm.Append("#main", "dagger", "hello", 0);

        // An empty picker reads as a list that failed to load, which is worse than saying nothing.
        Assert.Contains("hidden", vm.Model.VoiceSpeakersClass, StringComparison.Ordinal);
        Assert.Null(vm.CycleVoice("dagger"));
    }

    [Fact]
    public void ClickingASpeakerPinsThemAndTellsTheHost()
    {
        var vm = Room();
        var pinned = new List<(string Nick, string? Voice)>();
        var app = new BanterChatApp(vm) { VoicePinned = (n, v) => pinned.Add((n, v)) };
        vm.Append("#main", "dagger", "hello", 0);
        vm.ShowSettingsPanel(true);

        using var doc = app.CreateDocument();
        doc.Refresh();
        doc.BuildDisplayList(Width, Height);

        // Voices live in their own section now - the card cannot scroll, so the settings are
        // grouped and one group shows at a time. Reaching this control means choosing that
        // section, which is what somebody looking for it does.
        var (tabX, tabY) = PointOn(doc, "[data-settings-section=\"speaking\"]");
        doc.DispatchClick(tabX, tabY, 1);
        doc.BuildDisplayList(Width, Height);

        var (x, y) = PointOn(doc, "[data-voice-cycle=\"dagger\"]");
        doc.DispatchClick(x, y, 1);

        // The host is told so it can save it and apply it to what is speaking right now: choosing
        // a voice and having to restart to hear it is how you end up unsure whether it worked.
        Assert.Equal(("dagger", "aiden"), Assert.Single(pinned));
        Assert.Equal("aiden", vm.VoicePins["dagger"]);
    }

    [Fact]
    public void ChoosingAnEngineIsReadBackForSaving()
    {
        var vm = Room();
        Assert.Equal("local", vm.ChosenTranscribe);

        vm.ChooseTranscribe("wyoming");
        Assert.Equal("wyoming", vm.ChosenTranscribe);
    }

    /// <summary>
    /// Finds a control, scrolling the settings list to reach it if it is below the fold - which
    /// is what somebody looking for it does. The list is a fixed-height card with a scrolling
    /// body, so "not on screen right now" and "not reachable" are different answers, and only the
    /// second is a failure.
    /// </summary>
    private static (float X, float Y) PointOn(CupriDocument doc, string selector)
    {
        for (var attempt = 0; attempt < 12; attempt++)
        {
            if (TryPointOn(doc, selector) is { } found)
            {
                return found;
            }

            doc.DispatchWheel(Width / 2f, Height / 2f, -120);
        }

        throw new Xunit.Sdk.XunitException($"nothing painted matches {selector}, even after scrolling");
    }

    private static (float X, float Y)? TryPointOn(CupriDocument doc, string selector)
    {
        float minX = float.MaxValue, minY = float.MaxValue, maxX = -1, maxY = -1;
        for (var y = 0f; y < Height; y += 2)
        {
            for (var x = 0f; x < Width; x += 2)
            {
                if (doc.HitTest(x, y)?.Element?.Closest(selector) is null)
                {
                    continue;
                }

                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
            }
        }

        if (maxX < 0)
        {
            return null;
        }

        return ((minX + maxX) / 2, (minY + maxY) / 2);
    }
}
