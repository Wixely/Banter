using Banter.Protocol;

namespace Banter.Core;

/// <summary>A classified request, ready to route.</summary>
/// <param name="Sensitivity">How sensitive the content is. Unknown routes as sensitive.</param>
/// <param name="RequiredSkills">Skill tags the work needs; empty means any agent will do.</param>
/// <param name="AllowFrontier">
/// Static policy. When false, no frontier agent is eligible whatever the classification says —
/// a room or operator setting that a model's judgement cannot override (PLAN §8a).
/// </param>
public sealed record RoutingRequest(
    DataSensitivity Sensitivity,
    IReadOnlyList<string> RequiredSkills,
    bool AllowFrontier = true);

/// <summary>
/// Where a request should go, and why. <see cref="CrossesEgressBoundary"/> is true when any chosen
/// agent is not local, which is what the delegator must announce in the room before handing over.
/// </summary>
public sealed record RoutingDecision(
    IReadOnlyList<string> Agents,
    string Reason,
    bool CrossesEgressBoundary)
{
    public bool HasRecipients => Agents.Count > 0;

    public static RoutingDecision None(string reason) => new([], reason, false);
}

/// <summary>
/// Chooses which agent handles a request (PLAN §8a). Pure and deterministic, like
/// <see cref="DelegatorElection"/>, and for the same reason: this decides whether data leaves the
/// building, so it should be readable and testable without a model in the loop.
/// </summary>
public static class RequestRouting
{
    /// <summary>
    /// Pick the best agent for <paramref name="request"/>, or none.
    ///
    /// <para>Clearance is a hard filter applied first — an agent that may not receive the data is
    /// never a candidate no matter how well its skills match. Among those that may, ranking is
    /// skill coverage, then cost, then local-first, then join order.</para>
    /// </summary>
    public static RoutingDecision Choose(
        IReadOnlyList<AgentCandidate> roster,
        RoutingRequest request,
        string? excludeNick = null)
    {
        var effective = request.Sensitivity == DataSensitivity.Unknown
            ? DataSensitivity.Sensitive
            : request.Sensitivity;

        var eligible = roster
            .Where(a => !string.Equals(a.Nick, excludeNick, StringComparison.OrdinalIgnoreCase))
            .Where(a => DelegatorElection.CanReceive(a, effective))
            .Where(a => request.AllowFrontier || a.Locality == AgentLocality.Local)
            .ToList();

        if (eligible.Count == 0)
        {
            return RoutingDecision.None(
                $"no agent is cleared for {effective.ToString().ToLowerInvariant()} content");
        }

        var ranked = eligible
            .Select(a => (Agent: a, Covered: CoveredSkills(a, request.RequiredSkills)))
            .OrderByDescending(x => x.Covered)
            .ThenBy(x => x.Agent.CostTier)
            .ThenBy(x => x.Agent.Locality == AgentLocality.Local ? 0 : 1)
            .ThenBy(x => x.Agent.JoinSequence)
            .ToList();

        var best = ranked[0];

        // Nobody has the skills asked for. Say so rather than silently handing it to whoever was
        // cheapest — the delegator can then answer itself or ask the room.
        if (request.RequiredSkills.Count > 0 && best.Covered == 0)
        {
            return RoutingDecision.None(
                $"no agent has the skills for this ({string.Join(", ", request.RequiredSkills)})");
        }

        var reason = Describe(best.Agent, best.Covered, request, effective);
        return new RoutingDecision([best.Agent.Nick], reason, best.Agent.Locality != AgentLocality.Local);
    }

    /// <summary>
    /// Pick every eligible agent whose skills match, for work worth more than one opinion. Same
    /// clearance filter — fanning out never widens who may see the data.
    /// </summary>
    public static RoutingDecision ChooseAll(
        IReadOnlyList<AgentCandidate> roster,
        RoutingRequest request,
        string? excludeNick = null)
    {
        var effective = request.Sensitivity == DataSensitivity.Unknown
            ? DataSensitivity.Sensitive
            : request.Sensitivity;

        var matched = roster
            .Where(a => !string.Equals(a.Nick, excludeNick, StringComparison.OrdinalIgnoreCase))
            .Where(a => DelegatorElection.CanReceive(a, effective))
            .Where(a => request.AllowFrontier || a.Locality == AgentLocality.Local)
            .Where(a => request.RequiredSkills.Count == 0 || CoveredSkills(a, request.RequiredSkills) > 0)
            .OrderBy(a => a.CostTier)
            .ThenBy(a => a.JoinSequence)
            .ToList();

        if (matched.Count == 0)
        {
            return RoutingDecision.None("no eligible agent matched");
        }

        return new RoutingDecision(
            [.. matched.Select(a => a.Nick)],
            $"{matched.Count} agents cleared for {effective.ToString().ToLowerInvariant()} content",
            matched.Any(a => a.Locality != AgentLocality.Local));
    }

    private static int CoveredSkills(AgentCandidate agent, IReadOnlyList<string> required) =>
        required.Count == 0
            ? 0
            : required.Count(r => agent.Skills.Any(s => string.Equals(s, r, StringComparison.OrdinalIgnoreCase)));

    private static string Describe(
        AgentCandidate agent, int covered, RoutingRequest request, DataSensitivity effective)
    {
        var skillPart = covered > 0 && request.RequiredSkills.Count > 0
            ? $"has {string.Join("/", request.RequiredSkills.Where(r => agent.Skills.Contains(r, StringComparer.OrdinalIgnoreCase)))}"
            : "available";

        return agent.Locality == AgentLocality.Local
            ? $"{skillPart}, local"
            : $"{skillPart}, and this is {effective.ToString().ToLowerInvariant()} so it may leave our systems";
    }
}
