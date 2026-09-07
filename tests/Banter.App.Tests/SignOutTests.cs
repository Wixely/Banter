using Banter.App;
using Banter.Protocol;
using Xunit;

namespace Banter.App.Tests;

/// <summary>
/// Signing out, and the thing that makes it more than hiding a panel: the previous account's
/// conversation has to be gone, not merely off screen. Two people share a machine, one signs out
/// and the other signs in — anything left behind is the first one's private rooms shown to the
/// second.
/// </summary>
public sealed class SignOutTests
{
    private static ChatViewModel SignedIn()
    {
        var vm = new ChatViewModel();
        vm.ShowConnect("tcp://10.0.0.4:7770", "alice");
        vm.SetNick("alice");
        vm.SetStatus("Connected", connected: true);
        vm.Connected("tcp://10.0.0.4:7770", "alice");

        vm.AddRoom("#main");
        vm.SwitchTo("#main");
        vm.System("#main", "something alice said");
        vm.SetRoomListing([("#private", null, 2)]);

        return vm;
    }

    [Fact]
    public void ConnectingRecordsTheAccountAndOffersTheWayOut()
    {
        var vm = SignedIn();

        Assert.False(vm.ConnectVisible);
        Assert.Equal("tcp://10.0.0.4:7770", vm.Model.AccountServer);
        Assert.Equal("alice", vm.Model.AccountUser);
        Assert.DoesNotContain("hidden", vm.Model.SignOutClass, StringComparison.Ordinal);
    }

    [Fact]
    public void AHeadThatCannotSignOutNeverShowsTheControl()
    {
        // Connected() with no account is the old call, still used by anything handed its
        // credentials once and given no way to change them.
        var vm = new ChatViewModel();
        vm.Connected();

        Assert.Contains("hidden", vm.Model.SignOutClass, StringComparison.Ordinal);
    }

    [Fact]
    public void SigningOutReturnsToTheFormWithTheServerStillFilledIn()
    {
        var vm = SignedIn();

        vm.SignedOut("tcp://10.0.0.4:7770", "alice");

        Assert.True(vm.ConnectVisible);
        // The common reason to be here is a different account on the same server, so retyping the
        // address would be a poor reward.
        Assert.Equal("tcp://10.0.0.4:7770", vm.Model.ConnectServer);
        Assert.Equal("alice", vm.Model.ConnectUser);
        Assert.Equal("", vm.Model.ConnectPassword);
    }

    [Fact]
    public void SigningOutTakesTheConversationWithIt()
    {
        var vm = SignedIn();
        Assert.NotEmpty(vm.Model.Messages);
        Assert.NotEmpty(vm.Model.Rooms);

        vm.SignedOut("tcp://10.0.0.4:7770", "alice");

        Assert.Empty(vm.Model.Messages);
        Assert.Empty(vm.Model.Rooms);
        Assert.Empty(vm.Model.Browse);
        Assert.Equal("", vm.Model.ActiveRoom);
        Assert.Equal("", vm.Model.Nick);
    }

    [Fact]
    public void TheNextAccountDoesNotInheritTheBacklogOfTheLast()
    {
        // The one that a Model.Messages check alone would miss: the per-room backlog is a private
        // cache, and re-joining a room of the same name would otherwise repaint alice's messages
        // into bob's session.
        var vm = SignedIn();
        vm.SignedOut("tcp://10.0.0.4:7770", "alice");

        vm.Connected("tcp://10.0.0.4:7770", "bob");
        vm.AddRoom("#main");
        vm.SwitchTo("#main");

        Assert.Empty(vm.Model.Messages);
    }

    [Fact]
    public void ARefusalLandsOnTheFormAsItsReason()
    {
        var vm = SignedIn();

        vm.SignedOut("tcp://10.0.0.4:7770", "alice", "Refused: unknown account");

        Assert.True(vm.ConnectVisible);
        Assert.Equal("Refused: unknown account", vm.Model.ConnectStatus);
        Assert.Contains("hidden", vm.Model.SignOutClass, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSettingsPanelDoesNotSurviveSigningOut()
    {
        // It is where the sign-out button lives, so leaving it up would leave a panel about an
        // account nobody is signed in to sitting over the sign-in screen.
        var vm = SignedIn();
        vm.ShowSettingsPanel(true);

        vm.SignedOut("tcp://10.0.0.4:7770", "alice");

        Assert.Contains("hidden", vm.Model.SettingsPanelClass, StringComparison.Ordinal);
    }

    [Fact]
    public void TheQuestionIsAskedBeforeAnythingIsForgotten()
    {
        var vm = SignedIn();

        vm.ConfirmSignOut();
        Assert.True(vm.ConfirmOpen);

        // Cancelling leaves the session exactly as it was.
        vm.CancelConfirm();
        Assert.False(vm.ConfirmOpen);
        Assert.False(vm.TakeConfirmedSignOut());
        Assert.False(vm.ConnectVisible);
    }

    [Fact]
    public void ConfirmingIsTakenOnceAndOnlyOnce()
    {
        var vm = SignedIn();
        vm.ConfirmSignOut();

        Assert.True(vm.TakeConfirmedSignOut());

        // A second click on a dialog that is already gone must not sign out again.
        Assert.False(vm.TakeConfirmedSignOut());
        Assert.False(vm.ConfirmOpen);
    }

    [Fact]
    public void APendingRemovalIsNotMistakenForASignOut()
    {
        // The two share one dialog, and the sign-out check runs first. It must decline anything
        // that is not its own question rather than swallowing it.
        var vm = SignedIn();
        vm.SetUsers([new UserAccountPayload("bob", false)]);
        vm.SelectAdminUser("bob");
        vm.ConfirmRemoveUser();

        Assert.False(vm.TakeConfirmedSignOut());
        Assert.True(vm.ConfirmOpen);

        Assert.Equal((false, "bob"), vm.TakeConfirmed());
    }
}
