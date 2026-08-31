using Banter.App;
using Xunit;

namespace Banter.App.Tests;

/// <summary>
/// Every message row leads with a two-letter chip instead of a column of names, because a room is
/// read by scanning down its left edge and a letterform is recognisable where a name has to be
/// read. That only holds if the letters are the ones a reader would have picked themselves.
/// </summary>
public sealed class AvatarInitialsTests
{
    [Theory]
    [InlineData("alice", "AL")]
    [InlineData("bob", "BO")]
    [InlineData("local-a", "LO")]
    // Punctuation is common in agent nicks and carries nothing anyone can recognise: "[B" is not
    // a name, and every bracketed bot would wear the same chip.
    [InlineData("[bot]dagger", "BO")]
    [InlineData("_scout", "SC")]
    [InlineData("3rd-party", "3R")]
    // A nick can be shorter than the chip, or have nothing in it to show at all — a system line's
    // sender is a bare "*", and it must produce an empty chip rather than throw.
    [InlineData("j", "J")]
    [InlineData("*", "")]
    [InlineData("", "")]
    public void TheChipTakesTheFirstTwoLettersOrDigits(string nick, string expected) =>
        Assert.Equal(expected, ChatViewModel.InitialsOf(nick));

    [Fact]
    public void EveryMessageCarriesItsSendersChip()
    {
        var vm = new ChatViewModel();
        vm.SetNick("alice");
        vm.AddRoom("#main");

        vm.Append("#main", "bob", "hello", 0);

        Assert.Equal("BO", vm.Model.Messages[^1].Initials);
        Assert.Equal("AL", vm.Model.NickInitials);
    }

    [Fact]
    public void ASystemLineHasNoChipBecauseItHasNoAuthor()
    {
        var vm = new ChatViewModel();
        vm.AddRoom("#main");

        vm.System("#main", "dagger was elected delegator for this room");

        // Its sender is a placeholder, not a person. A chip reading "*" — or worse, one built from
        // the placeholder — would put an author beside a line that has none.
        Assert.Equal("", vm.Model.Messages[^1].Initials);
    }

    [Fact]
    public void TheChipFollowsARename()
    {
        var vm = new ChatViewModel();
        vm.SetNick("alice");

        vm.SetNick("dagger");

        Assert.Equal("DA", vm.Model.NickInitials);
    }
}
