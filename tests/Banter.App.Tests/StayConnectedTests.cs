using Banter.App;
using Xunit;

namespace Banter.App.Tests;

/// <summary>
/// The setting that asks a phone to hold its connection while it is in the background.
///
/// <para>It exists on one head and must be invisible on the others — not because the feature is
/// unfinished elsewhere, but because nothing takes a desktop's sockets away, so the control would
/// offer to solve a problem that host does not have.</para>
/// </summary>
public sealed class StayConnectedTests
{
    [Fact]
    public void AHostWithNoSuchProblemIsNotOfferedTheSetting()
    {
        var vm = new ChatViewModel();

        Assert.False(vm.CanStayConnected);
    }

    /// <summary>
    /// Empty rather than hidden. The scan button taught this the expensive way: a click-handled
    /// node at <c>display:none</c> is still counted by the focus walk, so it was eating a Tab on
    /// the sign-in form. A row that does not exist cannot.
    /// </summary>
    [Fact]
    public void AHostThatCannotDoItRendersNoRowToClickOrTabTo()
    {
        var vm = new ChatViewModel();

        vm.ShowSettingsSection("alerts");

        Assert.Empty(vm.Model.StayChoices);
    }

    [Fact]
    public void TheHeadThatHasTheProblemGetsTheSetting()
    {
        var vm = new ChatViewModel();

        vm.EnableStayConnected(false);

        Assert.True(vm.CanStayConnected);
        Assert.Equal(2, vm.Model.StayChoices.Count);
    }

    [Fact]
    public void ItStartsWhereverTheStoredSettingLeftIt()
    {
        var vm = new ChatViewModel();

        vm.EnableStayConnected(true);

        Assert.True(vm.StayConnected);
        Assert.Contains(vm.Model.StayChoices, r => r.Value == "stay" && r.RowClass.Contains("selected"));
    }

    [Fact]
    public void ChoosingToStayTurnsItOn()
    {
        var vm = new ChatViewModel();
        vm.EnableStayConnected(false);

        vm.ChooseStay("stay");

        Assert.True(vm.StayConnected);
        Assert.Contains(vm.Model.StayChoices, r => r.Value == "stay" && r.RowClass.Contains("selected"));
    }

    [Fact]
    public void ChoosingToDropTurnsItOff()
    {
        var vm = new ChatViewModel();
        vm.EnableStayConnected(true);

        vm.ChooseStay("drop");

        Assert.False(vm.StayConnected);
        Assert.Contains(vm.Model.StayChoices, r => r.Value == "drop" && r.RowClass.Contains("selected"));
    }

    /// <summary>
    /// Two rules decide one class, which is how a field ends up visible for one reason and
    /// invisible for another — the same trap the speakers field documents.
    /// </summary>
    [Fact]
    public void TheFieldIsHiddenByItsSectionAsWellAsByItsHost()
    {
        var vm = new ChatViewModel();
        vm.EnableStayConnected(true);

        vm.ShowSettingsSection("alerts");
        Assert.DoesNotContain("hidden", vm.Model.StayConnectedClass);

        vm.ShowSettingsSection("you");
        Assert.Contains("hidden", vm.Model.StayConnectedClass);
    }

    [Fact]
    public void TheFieldStaysHiddenOnAHostThatCannotDoItEvenInItsOwnSection()
    {
        var vm = new ChatViewModel();

        vm.ShowSettingsSection("alerts");

        Assert.Contains("hidden", vm.Model.StayConnectedClass);
    }

    /// <summary>
    /// The bug this guards: the phone's alerts page rendered "When you are named" and a hint above
    /// nothing at all, because the choices are seeded by the head and that head never did.
    /// </summary>
    [Fact]
    public void EnablingAlertsRendersTheChoiceRatherThanAHeadingAboveNothing()
    {
        var vm = new ChatViewModel();

        vm.EnableNotificationAlerts();

        Assert.NotEmpty(vm.Model.FlashChoices);
    }

    [Fact]
    public void AHostWithNoShadeTalksAboutItsTaskbar()
    {
        var vm = new ChatViewModel();

        vm.SetFlashOnMention(true);

        Assert.Contains(vm.Model.FlashChoices, r => r.Label.Contains("taskbar"));
    }

    /// <summary>"Flash the taskbar" on a phone describes nothing the phone does.</summary>
    [Fact]
    public void AHostWithAShadeDoesNot()
    {
        var vm = new ChatViewModel();

        vm.EnableNotificationAlerts();

        Assert.DoesNotContain(vm.Model.FlashChoices, r => r.Label.Contains("taskbar"));
        Assert.Contains(vm.Model.FlashChoices, r => r.Label == "Notify me");
    }

    [Fact]
    public void ChangingTheWordingDoesNotChangeTheChoice()
    {
        var vm = new ChatViewModel();
        vm.SetFlashOnMention(false);

        vm.EnableNotificationAlerts();

        Assert.False(vm.FlashOnMention);
        Assert.Contains(vm.Model.FlashChoices, r => r.Value == "quiet" && r.RowClass.Contains("selected"));
    }

    /// <summary>
    /// The head has a service to start and stop, and learns about it here. Without this the
    /// setting would be remembered and never acted on until the next sign-in.
    /// </summary>
    [Fact]
    public void TheHeadIsToldSoItCanStartOrStopTheService()
    {
        var vm = new ChatViewModel();
        vm.EnableStayConnected(false);

        var told = new List<bool>();
        var app = new BanterChatApp(vm) { StayConnectedChanged = on => told.Add(on) };

        app.ChooseStayConnected(true);
        app.ChooseStayConnected(false);

        Assert.Equal([true, false], told);
        Assert.False(vm.StayConnected);
    }
}
