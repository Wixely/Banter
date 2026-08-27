using Banter.App;
using Banter.Voice;
using Xunit;

namespace Banter.App.Tests;

/// <summary>
/// The voice controls as the user meets them — the button, the indicator, and what a finished
/// transcript does. No audio device is involved: the view model only ever reflects what a session
/// reports, which is the whole reason this is testable at all.
/// </summary>
public sealed class VoiceControlTests
{
    private static ChatViewModel Ready()
    {
        var vm = new ChatViewModel();
        vm.SetNick("alice");
        vm.AddRoom("#main");
        return vm;
    }

    [Fact]
    public void TheMicrophoneIsHiddenUntilAHeadWiresOne()
    {
        var vm = Ready();

        // A button that cannot do anything is worse than no button.
        Assert.Contains("hidden", vm.Model.MicClass);
        Assert.False(vm.VoiceAvailable);

        vm.EnableVoice(readbackAvailable: true);

        Assert.DoesNotContain("hidden", vm.Model.MicClass);
        Assert.DoesNotContain("hidden", vm.Model.ReadbackClass);
    }

    [Fact]
    public void AHeadWithNoSpeakerStillGetsAMicrophone()
    {
        var vm = Ready();

        vm.EnableVoice(readbackAvailable: false);

        Assert.DoesNotContain("hidden", vm.Model.MicClass);
        Assert.Contains("hidden", vm.Model.ReadbackClass);
    }

    [Theory]
    [InlineData(VoiceSessionState.Idle, "mic", "")]
    [InlineData(VoiceSessionState.Listening, "mic armed", "Listening")]
    [InlineData(VoiceSessionState.Capturing, "mic hearing", "Hearing you")]
    [InlineData(VoiceSessionState.Transcribing, "mic working", "Transcribing")]
    public void TheIndicatorReflectsWhatTheSessionIsDoing(
        VoiceSessionState state, string expectedClass, string expectedStatus)
    {
        var vm = Ready();
        vm.EnableVoice(readbackAvailable: true);

        vm.SetVoiceState(state);

        Assert.Equal(expectedClass, vm.Model.MicClass);
        Assert.Equal(expectedStatus, vm.Model.VoiceStatus);
    }

    [Fact]
    public void TheButtonSaysWhatTheNextTapWillDo()
    {
        var vm = Ready();
        vm.EnableVoice(readbackAvailable: true);

        vm.SetVoiceState(VoiceSessionState.Listening);
        Assert.Equal("Stop", vm.Model.MicText);

        vm.SetVoiceState(VoiceSessionState.Idle);
        Assert.Equal("Talk", vm.Model.MicText);
    }

    [Fact]
    public void ATranscriptIsSentStraightOutByDefault()
    {
        var vm = Ready();

        Assert.Equal("open the task board", vm.AcceptDraft("  open the task board  "));
        Assert.Equal("", vm.Model.Composer);
    }

    [Fact]
    public void ReviewBeforeSendPutsTheTranscriptInTheComposerInstead()
    {
        var vm = Ready();
        vm.ReviewBeforeSend = true;

        // Empty return means "nothing to send" — the caller sends what it is given and nothing
        // else, so the setting is honoured in exactly one place.
        Assert.Equal("", vm.AcceptDraft("open the task board"));
        Assert.Equal("open the task board", vm.Model.Composer);
    }

    [Fact]
    public void AReviewedTranscriptDoesNotEatAHalfTypedMessage()
    {
        var vm = Ready();
        vm.ReviewBeforeSend = true;
        vm.Model.Composer = "already typing";

        vm.AcceptDraft("and this too");

        Assert.Equal("already typing and this too", vm.Model.Composer);
    }

    [Fact]
    public void AnEmptyTranscriptIsNotSentAndDoesNotTouchTheComposer()
    {
        var vm = Ready();
        vm.Model.Composer = "typed";

        Assert.Equal("", vm.AcceptDraft("   "));
        Assert.Equal("typed", vm.Model.Composer);
    }

    [Fact]
    public void TheReadbackToggleCyclesAndSaysWhereItIs()
    {
        var vm = Ready();
        vm.EnableVoice(readbackAvailable: true);

        Assert.Equal(ReadbackPolicy.AgentsOnly, vm.Readback);
        Assert.Equal("Speech: agents", vm.Model.ReadbackText);

        Assert.Equal(ReadbackPolicy.Everyone, vm.CycleReadback());
        Assert.Equal("Speech: everyone", vm.Model.ReadbackText);

        Assert.Equal(ReadbackPolicy.Off, vm.CycleReadback());
        Assert.Equal("Speech: off", vm.Model.ReadbackText);

        Assert.Equal(ReadbackPolicy.AgentsOnly, vm.CycleReadback());
    }

    [Fact]
    public void TheRosterDecidesWhoCountsAsAnAgent()
    {
        var vm = Ready();
        vm.SetAgents("#main", [("dagger", true, "code", true)]);

        Assert.True(vm.IsAgent("dagger"));
        Assert.True(vm.IsAgent("DAGGER"));

        // Anyone not in the roster is a person, which is the safe answer for a speech policy.
        Assert.False(vm.IsAgent("bob"));
    }

    [Fact]
    public void YouAreRecognisedAsYourself()
    {
        var vm = Ready();

        Assert.True(vm.IsSelf("alice"));
        Assert.True(vm.IsSelf("Alice"));
        Assert.False(vm.IsSelf("dagger"));
    }

    [Fact]
    public void AVoiceFailureIsSaidInTheTimeline()
    {
        var vm = Ready();

        vm.VoiceFailed("the speech server refused");

        Assert.Contains(vm.Model.Messages, m => m.Text.Contains("[voice] the speech server refused"));
    }
}

/// <summary>The controls driven through a real document, the way the user clicks them.</summary>
public sealed class VoiceButtonTests
{
    private const int Width = 1100;
    private const int Height = 760;

    [Fact]
    public void TappingTheMicrophoneAsksTheHeadToOpenItThenCloseIt()
    {
        var vm = new ChatViewModel();
        vm.SetNick("alice");
        vm.AddRoom("#main");
        vm.EnableVoice(readbackAvailable: true);

        var asked = new List<bool>();
        var app = new BanterChatApp(vm) { VoiceToggleAsync = open => { asked.Add(open); return Task.CompletedTask; } };

        using var doc = app.CreateDocument();
        doc.BuildDisplayList(Width, Height);

        app.ToggleVoice();
        vm.SetVoiceState(VoiceSessionState.Listening);
        app.ToggleVoice();

        Assert.Equal([true, false], asked);
    }

    [Fact]
    public void TappingDoesNothingOnAHeadWithNoMicrophone()
    {
        var vm = new ChatViewModel();
        vm.AddRoom("#main");
        var asked = 0;
        var app = new BanterChatApp(vm) { VoiceToggleAsync = _ => { asked++; return Task.CompletedTask; } };

        app.ToggleVoice();

        Assert.Equal(0, asked);
    }

    [Fact]
    public void TheAppLaysOutWithTheVoiceControlsPresent()
    {
        var vm = new ChatViewModel();
        vm.SetNick("alice");
        vm.AddRoom("#main");
        vm.EnableVoice(readbackAvailable: true);
        vm.SetVoiceState(VoiceSessionState.Capturing);
        var app = new BanterChatApp(vm);

        using var doc = app.CreateDocument();
        doc.BuildDisplayList(Width, Height);
        var pixels = doc.RenderToPixels(Width, Height, SkiaSharp.SKColors.Black);

        // Catches markup or a binding path the voice row introduced that no longer resolves.
        Assert.Equal(Width * Height * 4, pixels.Length);
        Assert.Contains(pixels, b => b != 0);
    }

    [Fact]
    public void CyclingReadbackTellsTheHeadWhichPolicyIsNowInForce()
    {
        var vm = new ChatViewModel();
        vm.AddRoom("#main");
        vm.EnableVoice(readbackAvailable: true);

        var told = new List<ReadbackPolicy>();
        var app = new BanterChatApp(vm)
        {
            ReadbackChangedAsync = p => { told.Add(p); return Task.CompletedTask; },
        };

        app.CycleReadback();
        vm.ApplyPending();
        app.CycleReadback();
        vm.ApplyPending();

        Assert.Equal([ReadbackPolicy.Everyone, ReadbackPolicy.Off], told);
    }
}
