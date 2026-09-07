using Banter.App;
using Xunit;

namespace Banter.App.Tests;

/// <summary>
/// A message that names you, marked in the room and raised outside it.
///
/// <para>The rule matches what an agent counts as being summoned — an explicit <c>@nick</c>,
/// ending where the nick does — because a room that disagrees with itself about who was asked is
/// worse than one that never highlights anything.</para>
/// </summary>
public sealed class MentionHighlightTests
{
    private static ChatViewModel Room(string nick = "admin")
    {
        var vm = new ChatViewModel();
        vm.SetNick(nick);
        vm.AddRoom("#main");
        vm.SwitchTo("#main");
        return vm;
    }

    private static string ClassOfLast(ChatViewModel vm) => vm.Model.Messages[^1].RowClass;

    [Fact]
    public void BeingNamedMarksTheMessage()
    {
        var vm = Room();
        vm.Append("#main", "dev", "@admin I'm Codex, your AI coding collaborator.", 0);

        Assert.Contains("tome", ClassOfLast(vm), StringComparison.Ordinal);
    }

    [Fact]
    public void OrdinaryTrafficIsNotMarked()
    {
        var vm = Room();
        vm.Append("#main", "dev", "I've pushed the change.", 0);

        Assert.DoesNotContain("tome", ClassOfLast(vm), StringComparison.Ordinal);
    }

    [Fact]
    public void ANameInPassingIsNotBeingNamed()
    {
        // "ask admin about it" addresses the room. Marking it would make the mark mean nothing,
        // and the taskbar would flash at every sentence the word appeared in.
        var vm = Room();
        vm.Append("#main", "dev", "somebody should ask admin about it", 0);

        Assert.DoesNotContain("tome", ClassOfLast(vm), StringComparison.Ordinal);
        Assert.Equal(0, vm.MentionsSinceLooked);
    }

    [Fact]
    public void ALongerNameStartingWithYoursIsNotYou()
    {
        var vm = Room();
        vm.Append("#main", "dev", "@administrator should look", 0);

        Assert.DoesNotContain("tome", ClassOfLast(vm), StringComparison.Ordinal);
    }

    [Fact]
    public void PunctuationAfterTheNameStillCounts()
    {
        var vm = Room();
        vm.Append("#main", "dev", "@admin, can you look?", 0);

        Assert.Contains("tome", ClassOfLast(vm), StringComparison.Ordinal);
    }

    [Fact]
    public void YourOwnMessageDoesNotNameYou()
    {
        // Quoting a name back, or typing your own, must not flash your own taskbar.
        var vm = Room();
        vm.Append("#main", "admin", "@admin is me", 0);

        Assert.DoesNotContain("tome", ClassOfLast(vm), StringComparison.Ordinal);
        Assert.Equal(0, vm.MentionsSinceLooked);
    }

    [Fact]
    public void ASystemLineIsNotAMention()
    {
        var vm = Room();
        vm.System("#main", "@admin joined the room");

        Assert.DoesNotContain("tome", ClassOfLast(vm), StringComparison.Ordinal);
        Assert.Equal(0, vm.MentionsSinceLooked);
    }

    [Fact]
    public void SignedOutThereIsNoNameToMatch()
    {
        // Nick is empty before a session exists. Matching "@" against nothing would mark
        // everything, and history back-fills before the nick is known.
        var vm = new ChatViewModel();
        vm.AddRoom("#main");
        vm.SwitchTo("#main");
        vm.Append("#main", "dev", "@admin are you there?", 0);

        Assert.DoesNotContain("tome", ClassOfLast(vm), StringComparison.Ordinal);
    }

    [Fact]
    public void TheCountIsTakenOnceAndThenReset()
    {
        // The head acts on it, and acting twice on one mention is a taskbar that flashes for
        // something already read.
        var vm = Room();
        vm.Append("#main", "dev", "@admin one", 0);
        vm.Append("#main", "dev", "@admin two", 0);

        Assert.Equal(2, vm.MentionsSinceLooked);
        Assert.Equal(2, vm.TakeMentions());
        Assert.Equal(0, vm.MentionsSinceLooked);
        Assert.Equal(0, vm.TakeMentions());
    }

    [Fact]
    public void MentionsInAnotherRoomStillCount()
    {
        // Being named somewhere you are not looking is precisely when you need telling.
        var vm = Room();
        vm.AddRoom("#other");
        vm.Append("#other", "dev", "@admin over here", 0);

        Assert.Equal(1, vm.MentionsSinceLooked);
    }

    [Fact]
    public void TheSettingIsRememberedBothWays()
    {
        var vm = Room();

        vm.SetFlashOnMention(false);
        Assert.False(vm.FlashOnMention);

        vm.ChooseFlash("flash");
        Assert.True(vm.FlashOnMention);

        vm.ChooseFlash("quiet");
        Assert.False(vm.FlashOnMention);
    }
}
