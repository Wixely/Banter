using Banter.Protocol;
using Banter.Warden;
using Xunit;

namespace Banter.Server.Tests;

/// <summary>
/// Fleet configuration and its validation. The validation rules exist to catch combinations that
/// misbehave quietly rather than failing outright — the kind that look fine until a room does
/// something surprising.
/// </summary>
public sealed class FleetConfigTests
{
    private const string Minimal = """
        {
          "server": "tcp://127.0.0.1:7770",
          "agents": [
            { "user": "dagger", "model": "m", "locality": "local", "clearance": "sensitive" }
          ]
        }
        """;

    [Fact]
    public void AValidFleetHasNoProblems()
    {
        var fleet = FleetConfig.Parse(Minimal);

        Assert.Empty(fleet.Validate());
        Assert.Equal("tcp://127.0.0.1:7770", fleet.Server);
        Assert.Equal("dagger", Assert.Single(fleet.Agents).User);
    }

    [Fact]
    public void EnumsAreReadFromReadableNames()
    {
        var fleet = FleetConfig.Parse("""
            {
              "agents": [
                { "user": "scout", "model": "m", "locality": "frontier", "clearance": "public" }
              ]
            }
            """);

        var agent = Assert.Single(fleet.Agents);
        Assert.Equal(AgentLocality.Frontier, agent.Locality);
        Assert.Equal(DataSensitivity.Public, agent.Clearance);
    }

    [Fact]
    public void CommentsAndTrailingCommasAreAllowed()
    {
        // A fleet config is a thing humans edit, so it should tolerate being annotated.
        var fleet = FleetConfig.Parse("""
            {
              // the local one
              "agents": [
                { "user": "dagger", "model": "m", },
              ],
            }
            """);

        Assert.Single(fleet.Agents);
    }

    [Fact]
    public void AFrontierAgentClearedForSensitiveDataIsRejected()
    {
        var fleet = FleetConfig.Parse("""
            {
              "agents": [
                { "user": "claude", "model": "m", "locality": "frontier", "clearance": "sensitive" }
              ]
            }
            """);

        // This combination quietly sends private content to a third party; it should never be
        // something someone configures by accident.
        Assert.Contains(fleet.Validate(), p => p.Contains("frontier") && p.Contains("sensitive"));
    }

    [Fact]
    public void ADelegatorThatIsNotLocalIsRejected()
    {
        var fleet = FleetConfig.Parse("""
            {
              "agents": [
                { "user": "claude", "model": "m", "locality": "frontier", "clearance": "public",
                  "delegator": true }
              ]
            }
            """);

        // It would never be elected in a sensitive room, so the config is a mistake rather than
        // a preference.
        Assert.Contains(fleet.Validate(), p => p.Contains("delegator") && p.Contains("not local"));
    }

    [Fact]
    public void TheSameAccountTwiceIsRejected()
    {
        var fleet = FleetConfig.Parse("""
            {
              "agents": [
                { "user": "dagger", "model": "m" },
                { "user": "DAGGER", "model": "m" }
              ]
            }
            """);

        // Presence is per account, so two processes on one nick are one participant with two
        // brains answering.
        Assert.Contains(fleet.Validate(), p => p.Contains("more than once"));
    }

    [Fact]
    public void AnAgentWithNoModelIsRejected()
    {
        var fleet = FleetConfig.Parse("""{ "agents": [ { "user": "dagger" } ] }""");

        Assert.Contains(fleet.Validate(), p => p.Contains("no model"));
    }

    [Fact]
    public void AnEmptyFleetIsRejected() =>
        Assert.Contains(FleetConfig.Parse("""{ "agents": [] }""").Validate(), p => p.Contains("no agents"));

    [Fact]
    public void EveryProblemIsReportedNotJustTheFirst()
    {
        var fleet = FleetConfig.Parse("""
            {
              "agents": [
                { "user": "a" },
                { "user": "a", "locality": "frontier", "clearance": "sensitive" }
              ]
            }
            """);

        // Fixing a config one error per run is miserable.
        Assert.True(
            fleet.Validate().Count >= 3,
            "Expected the duplicate nick, both missing models, and the clearance clash.");
    }

    [Theory]
    [InlineData("dagger", "BANTER_AGENT_DAGGER_PASSWORD")]
    [InlineData("local-2", "BANTER_AGENT_LOCAL_2_PASSWORD")]
    [InlineData("a.b", "BANTER_AGENT_A_B_PASSWORD")]
    public void PasswordEnvironmentVariablesFollowAPredictableName(string user, string expected) =>
        Assert.Equal(expected, new AgentConfig { User = user }.ResolvedPasswordEnv);

    [Fact]
    public void AnExplicitPasswordVariableWins() =>
        Assert.Equal("MY_SECRET", new AgentConfig { User = "dagger", PasswordEnv = "MY_SECRET" }.ResolvedPasswordEnv);

    [Fact]
    public void BuildingOptionsWithoutAPasswordSaysWhichVariableIsMissing()
    {
        var fleet = FleetConfig.Parse(Minimal);

        var error = Assert.Throws<InvalidOperationException>(
            () => FleetSupervisor.BuildOptions(fleet, fleet.Agents[0]));

        // "Set this variable" is actionable; "no password" is not.
        Assert.Contains("BANTER_AGENT_DAGGER_PASSWORD", error.Message);
    }

    [Fact]
    public void NoSecretFieldExistsOnTheConfigAtAll()
    {
        // The file is meant to be committable, so it must not grow a password field later.
        var names = typeof(AgentConfig).GetProperties().Select(p => p.Name.ToLowerInvariant()).ToList();

        Assert.DoesNotContain(names, n => n is "password" or "secret" or "apikey" or "token");
    }

    [Fact]
    public void TheShippedSampleFleetIsValid()
    {
        // The sample is documentation, and documentation that does not work is worse than none.
        var path = Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "samples", "fleet.json");

        Assert.True(File.Exists(path), $"samples/fleet.json not found at {Path.GetFullPath(path)}");
        Assert.Empty(FleetConfig.Load(path).Validate());
    }
}
