using Xunit;

namespace Banter.Voice.Tests;

public sealed class ReadbackPolicyTests
{
    [Theory]
    [InlineData(ReadbackPolicy.Off, true, false)]
    [InlineData(ReadbackPolicy.Off, false, false)]
    [InlineData(ReadbackPolicy.AgentsOnly, true, true)]
    [InlineData(ReadbackPolicy.AgentsOnly, false, false)]
    [InlineData(ReadbackPolicy.Everyone, true, true)]
    [InlineData(ReadbackPolicy.Everyone, false, true)]
    public void ThePolicyDecidesWhoIsHeard(ReadbackPolicy policy, bool senderIsAgent, bool expected) =>
        Assert.Equal(expected, Readback.ShouldSpeak(policy, senderIsAgent, senderIsSelf: false));

    [Theory]
    [InlineData(ReadbackPolicy.Everyone)]
    [InlineData(ReadbackPolicy.AgentsOnly)]
    [InlineData(ReadbackPolicy.Off)]
    public void YourOwnMessagesAreNeverSpokenBackAtYou(ReadbackPolicy policy)
    {
        // Under always-listening this is a loop with no exit: spoken aloud, heard by the
        // microphone, transcribed, sent, spoken aloud.
        Assert.False(Readback.ShouldSpeak(policy, senderIsAgent: true, senderIsSelf: true));
        Assert.False(Readback.ShouldSpeak(policy, senderIsAgent: false, senderIsSelf: true));
    }
}

public sealed class VoiceAssignmentTests
{
    private static readonly VoiceDescriptor[] Pool =
        [new("alloy"), new("echo"), new("fable"), new("onyx"), new("nova"), new("shimmer")];

    [Fact]
    public void ASenderKeepsTheSameVoice()
    {
        var assignment = new VoiceAssignment(Pool);

        Assert.Equal(assignment.For("dagger"), assignment.For("dagger"));
    }

    [Fact]
    public void TheSameNameGetsTheSameVoiceInAFreshProcess()
    {
        // string.GetHashCode is seeded per process, so using it would deal the voices out again
        // on every restart and the room would sound like a different cast each morning.
        var monday = new VoiceAssignment(Pool);
        var tuesday = new VoiceAssignment(Pool);

        Assert.Equal(monday.For("warden"), tuesday.For("warden"));
    }

    [Fact]
    public void NoTwoAgentsShareAVoiceWhileThereAreVoicesLeft()
    {
        var assignment = new VoiceAssignment(Pool);
        var nicks = new[] { "dagger", "warden", "scribe", "local-a", "claude" };

        // Two agents that sound identical defeat the point of assigning voices at all, and a
        // plain hash collides at this size more often than not.
        Assert.Equal(nicks.Length, nicks.Select(assignment.For).Distinct().Count());
    }

    [Fact]
    public void PastTheSizeOfThePoolVoicesAreShared()
    {
        var assignment = new VoiceAssignment(Pool);

        var voices = Enumerable.Range(0, Pool.Length + 3).Select(i => assignment.For($"agent-{i}")).ToList();

        Assert.Equal(Pool.Length, voices.Distinct().Count());
    }

    [Fact]
    public void APinnedVoiceWins()
    {
        var assignment = new VoiceAssignment(Pool);
        assignment.Pin("dagger", "onyx");

        Assert.Equal("onyx", assignment.For("dagger"));
        Assert.Equal("onyx", assignment.For("DAGGER"));

        assignment.Unpin("dagger");
        Assert.Equal(new VoiceAssignment(Pool).For("dagger"), assignment.For("dagger"));
    }

    [Fact]
    public void CaseDoesNotChangeTheVoice()
    {
        var assignment = new VoiceAssignment(Pool);

        Assert.Equal(assignment.For("Dagger"), assignment.For("dagger"));
    }

    [Fact]
    public void ABackendWithNoVoicesGetsNoVoiceRatherThanAMadeUpOne()
    {
        var assignment = new VoiceAssignment([]);

        // Null means "send no voice and take the server's default", which is the honest request.
        Assert.Null(assignment.For("dagger"));
    }

    [Fact]
    public void EveryAssignedVoiceIsOneTheBackendOffered()
    {
        var assignment = new VoiceAssignment(Pool);
        var ids = Pool.Select(v => v.Id).ToList();

        foreach (var nick in Enumerable.Range(0, 200).Select(i => $"agent-{i}"))
        {
            Assert.Contains(assignment.For(nick), ids);
        }
    }
}
