using Banter.App;
using Xunit;

namespace Banter.App.Tests;

/// <summary>
/// What a finished transcript does, and the pause before it does it.
///
/// <para>Speech recognition is wrong often enough that sending without a chance to stop it posts
/// misheard sentences into a room full of people. The countdown is the chance; these are the ways
/// it can be taken, missed, or called off.</para>
///
/// <para>The clock is injected, so none of this waits for real seconds to pass.</para>
/// </summary>
public sealed class AutoSubmitTests
{
    private static (ChatViewModel Vm, Action<double> Advance) Room(bool autoSubmit = true, double delay = 3)
    {
        var vm = new ChatViewModel();
        vm.AddRoom("#main");
        vm.SwitchTo("#main");
        vm.SetAutoSubmit(autoSubmit, delay);

        var now = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        vm.Now = () => now;
        return (vm, seconds => now = now.AddSeconds(seconds));
    }

    [Fact]
    public void WithNoDelayItSendsAtOnce()
    {
        var (vm, _) = Room(delay: 0);

        Assert.Equal("book the room", vm.AcceptDraft("book the room"));
        Assert.Equal("", vm.Model.Composer);
        Assert.False(vm.SubmitPending);
    }

    [Fact]
    public void WithAutoSubmitOffItJustSitsThere()
    {
        var (vm, advance) = Room(autoSubmit: false);

        Assert.Equal("", vm.AcceptDraft("book the room"));
        Assert.Equal("book the room", vm.Model.Composer);
        Assert.False(vm.SubmitPending);

        advance(600);
        Assert.Equal("", vm.TakeDueSubmission());
        Assert.Equal("book the room", vm.Model.Composer);
    }

    [Fact]
    public void TheOldReviewSwitchStillMeansNeverSendByItself()
    {
        // A settings file written before auto-submit existed. Honouring the new default over it
        // would start posting for somebody who had asked it not to.
        var (vm, advance) = Room();
        vm.ReviewBeforeSend = true;

        Assert.Equal("", vm.AcceptDraft("book the room"));
        Assert.False(vm.SubmitPending);

        advance(600);
        Assert.Equal("", vm.TakeDueSubmission());
    }

    [Fact]
    public void WithADelayItWaitsAndThenSends()
    {
        var (vm, advance) = Room(delay: 3);

        Assert.Equal("", vm.AcceptDraft("book the room"));
        Assert.True(vm.SubmitPending);
        Assert.Equal("book the room", vm.Model.Composer);
        Assert.Contains("Sending in 3s", vm.Model.PendingSubmitText, StringComparison.Ordinal);

        advance(1);
        Assert.Equal("", vm.TakeDueSubmission());
        Assert.Contains("Sending in 2s", vm.Model.PendingSubmitText, StringComparison.Ordinal);

        advance(2);
        Assert.Equal("book the room", vm.TakeDueSubmission());
        Assert.Equal("", vm.Model.Composer);
        Assert.False(vm.SubmitPending);
    }

    [Fact]
    public void CancellingLeavesTheWordsToBeEdited()
    {
        // Cancel means "do not send that", not "throw away what I said" - the alternative is
        // losing a sentence somebody has no other copy of.
        var (vm, advance) = Room();
        vm.AcceptDraft("book the rrom");

        vm.CancelPendingSubmit();

        Assert.False(vm.SubmitPending);
        Assert.Equal("book the rrom", vm.Model.Composer);
        Assert.Contains("hidden", vm.Model.PendingSubmitClass, StringComparison.Ordinal);

        advance(600);
        Assert.Equal("", vm.TakeDueSubmission());
    }

    [Fact]
    public void TypingCancelsIt()
    {
        // Somebody correcting a transcript is somebody who intends to look at it. Sending out
        // from under them mid-edit is the worst thing this feature could do.
        var (vm, advance) = Room();
        vm.AcceptDraft("book the rrom");

        vm.Model.Composer = "book the room";       // the engine writes typing straight into this
        advance(600);

        Assert.Equal("", vm.TakeDueSubmission());
        Assert.False(vm.SubmitPending);
        Assert.Equal("book the room", vm.Model.Composer);
    }

    [Fact]
    public void SendNowSkipsTheRestOfTheWait()
    {
        var (vm, _) = Room();
        vm.AcceptDraft("book the room");

        Assert.Equal("book the room", vm.TakePendingNow());
        Assert.Equal("", vm.Model.Composer);
        Assert.False(vm.SubmitPending);
    }

    [Fact]
    public void SendNowWithNothingWaitingSendsNothing()
    {
        var (vm, _) = Room();
        vm.Model.Composer = "typed by hand";

        Assert.Equal("", vm.TakePendingNow());
        Assert.Equal("typed by hand", vm.Model.Composer);
    }

    [Fact]
    public void ASecondTranscriptRestartsTheWait()
    {
        // Two utterances in a row: the second must not inherit the first one's deadline and go
        // out half a second after it was spoken.
        var (vm, advance) = Room(delay: 3);
        vm.AcceptDraft("book the room");

        advance(2);
        vm.AcceptDraft("for tomorrow");

        Assert.Equal("book the room for tomorrow", vm.Model.Composer);
        Assert.Contains("Sending in 3s", vm.Model.PendingSubmitText, StringComparison.Ordinal);

        advance(2);
        Assert.Equal("", vm.TakeDueSubmission());       // 2s into the new wait, not 4s into the old

        advance(1);
        Assert.Equal("book the room for tomorrow", vm.TakeDueSubmission());
    }

    [Fact]
    public void ADraftIsAppendedToWhatWasAlreadyTyped()
    {
        var (vm, _) = Room();
        vm.Model.Composer = "half typed";

        vm.AcceptDraft("and spoken");

        Assert.Equal("half typed and spoken", vm.Model.Composer);
    }

    [Fact]
    public void SilenceProducesNothing()
    {
        var (vm, _) = Room();

        Assert.Equal("", vm.AcceptDraft("   "));
        Assert.False(vm.SubmitPending);
        Assert.Equal("", vm.Model.Composer);
    }

    [Fact]
    public void TurningAutoSubmitOffStopsACountdownAlreadyRunning()
    {
        var (vm, advance) = Room();
        vm.AcceptDraft("book the room");
        Assert.True(vm.SubmitPending);

        vm.ChooseAutoSubmit("hold");

        Assert.False(vm.SubmitPending);
        advance(600);
        Assert.Equal("", vm.TakeDueSubmission());
    }

    [Theory]
    [InlineData("5", 5)]
    [InlineData("0", 0)]
    [InlineData("2.5", 2.5)]
    [InlineData("banana", 3)]        // unparseable keeps what it had
    [InlineData("-1", 3)]            // negative is not a wait
    [InlineData("9999", 3)]          // beyond the cap
    public void TheDelayBoxRefusesToSilentlyBecomeZero(string typed, double expected)
    {
        var (vm, _) = Room(delay: 3);
        vm.Model.AutoSubmitDelay = typed;

        Assert.Equal(expected, vm.ReadAutoSubmitDelay());
    }
}
