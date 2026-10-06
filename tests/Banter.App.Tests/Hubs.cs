using Banter.Protocol;

namespace Banter.App.Tests;

/// <summary>A hub to show the hubs page, shared so <see cref="AppPages"/> and the page's own
/// tests describe the same deployment.</summary>
public static class Hubs
{
    public static HubAgentPayload Agent(string nick, params string[] tools) =>
        new(nick, $"usr_{nick}", DateTimeOffset.UtcNow.AddDays(-2).ToUnixTimeMilliseconds(), tools);

    public static HubPayload Sample { get; } = new(
        "mcphub", "MCPHub", "Connected", "", 17, true,
        [Agent("scribe", "mcphub__gh_list_issues", "mcphub__gh_get_issue"), Agent("dagger")]);
}
