using Banter.Core;
using Banter.Protocol;
using Xunit;

namespace Banter.Server.Tests;

/// <summary>
/// Routing and classification (PLAN §8a). As with the election, the interesting cases are the
/// ones where something is unclear — those are the ones that decide whether data leaves.
/// </summary>
public sealed class RequestRoutingTests
{
    private static AgentCandidate Agent(
        string nick,
        AgentLocality locality = AgentLocality.Local,
        DataSensitivity clearance = DataSensitivity.Sensitive,
        string[]? skills = null,
        int cost = 1,
        long join = 0) =>
        new(nick, locality, clearance, skills ?? ["chat"], cost, join);

    private static readonly AgentCandidate LocalGeneralist =
        Agent("local", skills: ["chat", "email"], cost: 1, join: 0);

    private static readonly AgentCandidate FrontierResearcher =
        Agent("claude", AgentLocality.Frontier, DataSensitivity.Public, ["web", "github", "research"], cost: 5, join: 1);

    // ── Routing ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void APublicResearchQuestionCanGoToAFrontierAgent()
    {
        var decision = RequestRouting.Choose(
            [LocalGeneralist, FrontierResearcher],
            new RoutingRequest(DataSensitivity.Public, ["github"]));

        Assert.Equal(["claude"], decision.Agents);
        Assert.True(decision.CrossesEgressBoundary);
        Assert.Contains("leave our systems", decision.Reason);
    }

    [Fact]
    public void ASensitiveRequestNeverReachesAFrontierAgentEvenWhenItIsTheOnlySkillMatch()
    {
        // The frontier agent is the only one with 'github', but it is not cleared. Skills never
        // outrank clearance.
        var decision = RequestRouting.Choose(
            [LocalGeneralist, FrontierResearcher],
            new RoutingRequest(DataSensitivity.Sensitive, ["github"]));

        Assert.False(decision.HasRecipients);
        Assert.False(decision.CrossesEgressBoundary);
    }

    [Fact]
    public void AnUnclassifiedRequestIsRoutedAsSensitive()
    {
        var decision = RequestRouting.Choose(
            [LocalGeneralist, FrontierResearcher],
            new RoutingRequest(DataSensitivity.Unknown, []));

        Assert.Equal(["local"], decision.Agents);
        Assert.False(decision.CrossesEgressBoundary);
    }

    [Fact]
    public void StaticPolicyBeatsTheClassification()
    {
        // Classified public, but the room forbids frontier routing - the setting wins.
        var decision = RequestRouting.Choose(
            [FrontierResearcher],
            new RoutingRequest(DataSensitivity.Public, ["web"], AllowFrontier: false));

        Assert.False(decision.HasRecipients);
    }

    [Fact]
    public void SkillCoverageOutranksCost()
    {
        var cheapGeneralist = Agent("cheap", skills: ["chat"], cost: 1, join: 0);
        var dearSpecialist = Agent("specialist", skills: ["code"], cost: 9, join: 1);

        var decision = RequestRouting.Choose(
            [cheapGeneralist, dearSpecialist],
            new RoutingRequest(DataSensitivity.Sensitive, ["code"]));

        Assert.Equal(["specialist"], decision.Agents);
    }

    [Fact]
    public void NoSkillMatchReportsThatRatherThanPickingTheCheapest()
    {
        var decision = RequestRouting.Choose(
            [LocalGeneralist],
            new RoutingRequest(DataSensitivity.Sensitive, ["vision"]));

        Assert.False(decision.HasRecipients);
        Assert.Contains("vision", decision.Reason);
    }

    [Fact]
    public void TheDelegatorCanExcludeItselfFromItsOwnRouting()
    {
        var decision = RequestRouting.Choose(
            [LocalGeneralist],
            new RoutingRequest(DataSensitivity.Sensitive, []),
            excludeNick: "local");

        Assert.False(decision.HasRecipients);
    }

    [Fact]
    public void FanningOutDoesNotWidenWhoMaySeeTheData()
    {
        var otherLocal = Agent("local-2", skills: ["chat"], cost: 2, join: 2);

        var decision = RequestRouting.ChooseAll(
            [LocalGeneralist, FrontierResearcher, otherLocal],
            new RoutingRequest(DataSensitivity.Sensitive, []));

        Assert.Equal(["local", "local-2"], decision.Agents);
        Assert.False(decision.CrossesEgressBoundary);
    }

    // ── Classification ───────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("can you check my email for the invoice")]
    [InlineData("what is our customer's password reset flow")]
    [InlineData("look at the production database")]
    public async Task RequestsTouchingOurSystemsClassifyAsSensitive(string text)
    {
        var result = await new KeywordRequestClassifier().ClassifyAsync(text);

        Assert.Equal(DataSensitivity.Sensitive, result.Sensitivity);
    }

    [Theory]
    [InlineData("what is the traffic like on the M50")]
    [InlineData("explain how a b-tree works")]
    [InlineData("what is the weather tomorrow")]
    public async Task GeneralKnowledgeQuestionsClassifyAsPublic(string text)
    {
        var result = await new KeywordRequestClassifier().ClassifyAsync(text);

        Assert.Equal(DataSensitivity.Public, result.Sensitivity);
    }

    [Fact]
    public async Task AnythingUnrecognisedIsTreatedAsSensitive()
    {
        // The single most important behaviour here: silence is not consent to send data out.
        var result = await new KeywordRequestClassifier().ClassifyAsync("handle the Thompson matter");

        Assert.Equal(DataSensitivity.Sensitive, result.Sensitivity);
        Assert.Contains("treating it as sensitive", result.Rationale);
    }

    [Fact]
    public async Task ASensitiveSignalBeatsAPublicOneInTheSameSentence()
    {
        // "what is" would read as public on its own; "customer" must win.
        var result = await new KeywordRequestClassifier().ClassifyAsync("what is our customer's address");

        Assert.Equal(DataSensitivity.Sensitive, result.Sensitivity);
    }

    [Fact]
    public async Task SkillsAreDetectedAlongsideSensitivity()
    {
        var result = await new KeywordRequestClassifier().ClassifyAsync("open a github pull request for this bug");

        Assert.Contains("github", result.Skills);
        Assert.Contains("code", result.Skills);
    }

    [Fact]
    public async Task ARoomFloorRaisesAClassificationButNeverLowersIt()
    {
        var floored = new FlooredClassifier(new KeywordRequestClassifier(), DataSensitivity.Internal);

        var raised = await floored.ClassifyAsync("what is the weather tomorrow");
        Assert.Equal(DataSensitivity.Internal, raised.Sensitivity);
        Assert.Contains("room policy", raised.Rationale);

        // Already above the floor: left alone, not pulled down to it.
        var untouched = await floored.ClassifyAsync("check my email");
        Assert.Equal(DataSensitivity.Sensitive, untouched.Sensitivity);
    }

    [Fact]
    public async Task ClassificationFeedsRoutingEndToEnd()
    {
        var classifier = new KeywordRequestClassifier();

        var sensitive = await classifier.ClassifyAsync("summarise my email inbox");
        var sensitiveRoute = RequestRouting.Choose(
            [LocalGeneralist, FrontierResearcher],
            new RoutingRequest(sensitive.Sensitivity, sensitive.Skills));
        Assert.Equal(["local"], sensitiveRoute.Agents);
        Assert.False(sensitiveRoute.CrossesEgressBoundary);

        var open = await classifier.ClassifyAsync("search github for the public issue about this");
        var openRoute = RequestRouting.Choose(
            [LocalGeneralist, FrontierResearcher],
            new RoutingRequest(open.Sensitivity, open.Skills));
        Assert.Equal(["claude"], openRoute.Agents);
        Assert.True(openRoute.CrossesEgressBoundary);
    }
}
