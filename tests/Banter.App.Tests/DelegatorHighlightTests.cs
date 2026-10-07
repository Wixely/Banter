using Banter.App;
using Xunit;
using Xunit.Abstractions;

namespace Banter.App.Tests;

/// <summary>
/// The room's dispatcher is visible in the timeline, not only in the header.
///
/// <para>"Who is answering" is the question a room full of agents raises, and the delegator is the
/// one that decides. It was readable only from the header, in words, for the active room - so a
/// line in the timeline said nothing about which agent it came from in that sense.</para>
/// </summary>
public sealed class DelegatorHighlightTests(ITestOutputHelper output)
{
    private static ChatViewModel Room()
    {
        var vm = new ChatViewModel();
        vm.SetNick("alice");
        vm.AddRoom("#main");
        vm.SwitchTo("#main");
        vm.Connected("tcp://h:7770", "alice");
        return vm;
    }

    private static string ClassOf(ChatViewModel vm, string sender) =>
        vm.Model.Messages.Last(m => m.Sender == sender).RowClass;

    [Fact]
    public void TheDelegatorsLinesAreMarked()
    {
        var vm = Room();
        vm.SetDelegator("#main", "dagger");
        vm.Append("#main", "dagger", "I'll take this one", 0);
        vm.Append("#main", "scribe", "righto", 0);

        output.WriteLine($"dagger: {ClassOf(vm, "dagger")} / scribe: {ClassOf(vm, "scribe")}");

        Assert.Contains("delegator", ClassOf(vm, "dagger"), StringComparison.Ordinal);
        Assert.DoesNotContain("delegator", ClassOf(vm, "scribe"), StringComparison.Ordinal);
    }

    /// <summary>
    /// What was already said is re-marked. An election moves the role mid-conversation, and the
    /// room wants to know who is dispatching NOW rather than who was when a line arrived.
    /// </summary>
    [Fact]
    public void AnElectionRemarksTheBacklog()
    {
        var vm = Room();
        vm.SetDelegator("#main", "dagger");
        vm.Append("#main", "dagger", "mine", 0);
        vm.Append("#main", "scribe", "ok", 0);
        Assert.Contains("delegator", ClassOf(vm, "dagger"), StringComparison.Ordinal);

        vm.SetDelegator("#main", "scribe");

        output.WriteLine($"after: dagger={ClassOf(vm, "dagger")} scribe={ClassOf(vm, "scribe")}");
        Assert.DoesNotContain("delegator", ClassOf(vm, "dagger"), StringComparison.Ordinal);
        Assert.Contains("delegator", ClassOf(vm, "scribe"), StringComparison.Ordinal);
    }

    /// <summary>A room with nobody dispatching marks nothing, and losing the delegator clears
    /// what was marked rather than leaving a colour nothing stands behind.</summary>
    [Fact]
    public void NoDelegatorMarksNothing()
    {
        var vm = Room();
        vm.Append("#main", "dagger", "anyone?", 0);
        Assert.DoesNotContain("delegator", ClassOf(vm, "dagger"), StringComparison.Ordinal);

        vm.SetDelegator("#main", "dagger");
        Assert.Contains("delegator", ClassOf(vm, "dagger"), StringComparison.Ordinal);

        vm.SetDelegator("#main", null);
        Assert.DoesNotContain("delegator", ClassOf(vm, "dagger"), StringComparison.Ordinal);
    }

    /// <summary>
    /// Per room, and not the active one's answer applied everywhere. A delegator in one room says
    /// nothing about an agent of the same name in another.
    /// </summary>
    [Fact]
    public void TheMarkFollowsTheRoomTheMessageIsIn()
    {
        var vm = Room();
        vm.AddRoom("#notes");
        vm.SetDelegator("#main", "dagger");
        vm.SetDelegator("#notes", "scribe");

        vm.Append("#main", "dagger", "in main", 0);
        vm.Append("#notes", "dagger", "in notes", 0);

        Assert.Contains("delegator", ClassOf(vm, "dagger"), StringComparison.Ordinal);

        vm.SwitchTo("#notes");
        output.WriteLine($"in #notes, dagger: {ClassOf(vm, "dagger")}");
        Assert.DoesNotContain("delegator", ClassOf(vm, "dagger"), StringComparison.Ordinal);
    }

    /// <summary>A system line has no author, so it is never anybody's.</summary>
    [Fact]
    public void ASystemLineIsNeverTheDelegators()
    {
        var vm = Room();
        vm.SetDelegator("#main", "dagger");
        vm.System("#main", "dagger joined");

        Assert.DoesNotContain("delegator", vm.Model.Messages.Last().RowClass, StringComparison.Ordinal);
    }
}
