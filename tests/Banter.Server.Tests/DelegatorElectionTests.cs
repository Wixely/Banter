using Banter.Core;
using Banter.Protocol;
using Xunit;

namespace Banter.Server.Tests;

/// <summary>
/// The election governs which agent reads every message in a room, so these cover the
/// fail-closed rules from PLAN §8a as much as the happy path.
/// </summary>
public sealed class DelegatorElectionTests
{
    private static AgentCandidate Agent(
        string nick,
        AgentLocality locality = AgentLocality.Local,
        DataSensitivity clearance = DataSensitivity.Sensitive,
        int cost = 1,
        long join = 0,
        bool configured = false) =>
        new(nick, locality, clearance, ["chat"], cost, join, configured);

    [Fact]
    public void AnEmptyRoomElectsNobody()
    {
        var result = DelegatorElection.Elect([]);

        Assert.Null(result.Nick);
        Assert.Contains("no agents", result.Reason);
    }

    [Fact]
    public void AConfiguredDelegatorWinsOutright()
    {
        var result = DelegatorElection.Elect([
            Agent("local-a", join: 0),
            Agent("chosen", cost: 99, join: 5, configured: true),
        ]);

        Assert.Equal("chosen", result.Nick);
        Assert.Contains("configured", result.Reason);
    }

    [Fact]
    public void LocalAgentsArePreferredOverFrontierOnes()
    {
        var result = DelegatorElection.Elect(
            [
                Agent("claude", AgentLocality.Frontier, DataSensitivity.Sensitive, join: 0),
                Agent("local", AgentLocality.Local, DataSensitivity.Sensitive, join: 1),
            ],
            DataSensitivity.Public);

        Assert.Equal("local", result.Nick);
    }

    [Fact]
    public void AFrontierAgentCannotDelegateForASensitiveRoom()
    {
        // The delegator reads every message before classifying any of them, so a frontier
        // delegator has already seen the content it was supposed to keep local.
        var result = DelegatorElection.Elect(
            [Agent("claude", AgentLocality.Frontier, DataSensitivity.Sensitive)],
            DataSensitivity.Sensitive);

        Assert.Null(result.Nick);
        Assert.Contains("cleared", result.Reason);
    }

    [Fact]
    public void NobodyIsElectedRatherThanSomebodyIneligible()
    {
        // A room with only frontier agents gets no delegator and keeps working in mention mode.
        // Electing one anyway would leak every message in the room.
        var result = DelegatorElection.Elect(
            [
                Agent("claude", AgentLocality.Frontier, DataSensitivity.Public),
                Agent("codex", AgentLocality.Frontier, DataSensitivity.Public),
            ],
            DataSensitivity.Internal);

        Assert.Null(result.Nick);
    }

    [Fact]
    public void UnknownLocalityIsTreatedAsFrontier()
    {
        // Assuming an unstated agent is local is exactly the mistake that leaks data.
        var result = DelegatorElection.Elect(
            [Agent("mystery", AgentLocality.Unknown, DataSensitivity.Sensitive)],
            DataSensitivity.Sensitive);

        Assert.Null(result.Nick);
    }

    [Fact]
    public void UnknownClearanceIsTreatedAsNoClearance()
    {
        var result = DelegatorElection.Elect(
            [Agent("mystery", AgentLocality.Local, DataSensitivity.Unknown)],
            DataSensitivity.Internal);

        Assert.Null(result.Nick);
    }

    [Fact]
    public void AnUnclassifiedRoomIsTreatedAsSensitive()
    {
        // Default room sensitivity, and an agent cleared only for internal data.
        var result = DelegatorElection.Elect([Agent("local", clearance: DataSensitivity.Internal)]);

        Assert.Null(result.Nick);
    }

    [Fact]
    public void APublicRoomDoesNotConstrainLocality()
    {
        var result = DelegatorElection.Elect(
            [Agent("claude", AgentLocality.Frontier, DataSensitivity.Public)],
            DataSensitivity.Public);

        Assert.Equal("claude", result.Nick);
    }

    [Fact]
    public void TiesBreakOnClearanceThenCostThenJoinOrder()
    {
        var byClearance = DelegatorElection.Elect(
            [
                Agent("lower", clearance: DataSensitivity.Internal, join: 0),
                Agent("higher", clearance: DataSensitivity.Sensitive, join: 1),
            ],
            DataSensitivity.Internal);
        Assert.Equal("higher", byClearance.Nick);

        var byCost = DelegatorElection.Elect(
            [Agent("dear", cost: 9, join: 0), Agent("cheap", cost: 1, join: 1)],
            DataSensitivity.Internal);
        Assert.Equal("cheap", byCost.Nick);

        var byJoin = DelegatorElection.Elect(
            [Agent("second", join: 2), Agent("first", join: 1)],
            DataSensitivity.Internal);
        Assert.Equal("first", byJoin.Nick);
    }

    [Fact]
    public void ElectionIsDeterministicSoReconnectsDoNotFlapTheDelegator()
    {
        var roster = new[] { Agent("a", join: 3), Agent("b", join: 1), Agent("c", join: 2) };

        var first = DelegatorElection.Elect(roster, DataSensitivity.Internal);
        var reordered = DelegatorElection.Elect([roster[2], roster[0], roster[1]], DataSensitivity.Internal);

        Assert.Equal(first.Nick, reordered.Nick);
        Assert.Equal("b", first.Nick);
    }

    [Theory]
    [InlineData(AgentLocality.Local, DataSensitivity.Sensitive, DataSensitivity.Sensitive, true)]
    [InlineData(AgentLocality.Local, DataSensitivity.Internal, DataSensitivity.Sensitive, false)]
    [InlineData(AgentLocality.Frontier, DataSensitivity.Public, DataSensitivity.Public, true)]
    [InlineData(AgentLocality.Frontier, DataSensitivity.Sensitive, DataSensitivity.Internal, false)]
    [InlineData(AgentLocality.Local, DataSensitivity.Public, DataSensitivity.Public, true)]
    public void RoutingAllowsOnlyAgentsClearedForTheRequest(
        AgentLocality locality, DataSensitivity clearance, DataSensitivity request, bool allowed) =>
        Assert.Equal(allowed, DelegatorElection.CanReceive(Agent("a", locality, clearance), request));

    [Fact]
    public void AnUnclassifiedRequestIsRoutedAsSensitiveSoItStaysLocal()
    {
        var frontier = Agent("claude", AgentLocality.Frontier, DataSensitivity.Sensitive);
        var local = Agent("local", AgentLocality.Local, DataSensitivity.Sensitive);

        Assert.False(DelegatorElection.CanReceive(frontier, DataSensitivity.Unknown));
        Assert.True(DelegatorElection.CanReceive(local, DataSensitivity.Unknown));
    }
}
