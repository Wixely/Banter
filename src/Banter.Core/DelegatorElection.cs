using Banter.Protocol;

namespace Banter.Core;

/// <summary>One agent in a room, as the election sees it.</summary>
/// <param name="Nick">Agent's nick.</param>
/// <param name="Locality">Local or frontier.</param>
/// <param name="Clearance">Most sensitive data it may receive.</param>
/// <param name="Skills">Capability tags.</param>
/// <param name="CostTier">Lower is cheaper; a tie-break only.</param>
/// <param name="JoinSequence">Monotonic join order, for a deterministic final tie-break.</param>
/// <param name="ConfiguredDelegator">Operator named this agent the room's delegator.</param>
public sealed record AgentCandidate(
    string Nick,
    AgentLocality Locality,
    DataSensitivity Clearance,
    IReadOnlyList<string> Skills,
    int CostTier,
    long JoinSequence,
    bool ConfiguredDelegator = false,
    AgentWorkMode WorkMode = AgentWorkMode.DelegateAndWork);

/// <summary>Who won, and why — the reason is announced into the room.</summary>
public sealed record ElectionResult(string? Nick, string Reason);

/// <summary>
/// Elects a room's delegator (PLAN §8a). Pure and deterministic: the same roster always elects
/// the same agent, which is what makes re-election after a reconnect stable instead of flapping.
///
/// <para>Kept out of the room engine so the policy can be reasoned about and tested on its own —
/// it is the decision that governs whether data can leave the building.</para>
/// </summary>
public static class DelegatorElection
{
    /// <summary>
    /// Pick a delegator from <paramref name="candidates"/>.
    ///
    /// <para>Order: an operator-configured delegator wins outright. Otherwise <b>local agents are
    /// preferred over frontier ones</b>, and in a room that permits sensitive content a frontier
    /// agent is not eligible at all — the delegator reads every message before classifying any of
    /// them, so a frontier delegator has already seen the sensitive content it was supposed to
    /// keep local. Remaining ties break on clearance (higher first), then cost (cheaper first),
    /// then join order.</para>
    /// </summary>
    /// <param name="candidates">Agents currently in the room.</param>
    /// <param name="roomSensitivity">
    /// The most sensitive content this room may carry. Defaults to <see cref="DataSensitivity.Sensitive"/>
    /// because an unclassified room must be assumed to be the risky one.
    /// </param>
    public static ElectionResult Elect(
        IReadOnlyList<AgentCandidate> candidates,
        DataSensitivity roomSensitivity = DataSensitivity.Sensitive)
    {
        if (candidates.Count == 0)
        {
            return new ElectionResult(null, "no agents in the room");
        }

        var configured = candidates
            .Where(c => c.ConfiguredDelegator)
            .OrderBy(c => c.JoinSequence)
            .FirstOrDefault();
        if (configured is not null)
        {
            return new ElectionResult(configured.Nick, "configured as this room's delegator");
        }

        var eligible = candidates.Where(c => CanDelegateFor(c, roomSensitivity)).ToList();

        if (eligible.Count == 0)
        {
            // Deliberately elect nobody rather than fall back to an ineligible agent. No
            // delegator means the room keeps working in mention mode; the wrong delegator means
            // every message in the room is read by something that should not see it.
            return new ElectionResult(
                null,
                $"no agent is cleared for {roomSensitivity.ToString().ToLowerInvariant()} content in this room");
        }

        var winner = eligible
            .OrderBy(c => c.Locality == AgentLocality.Local ? 0 : 1)
            .ThenByDescending(c => c.Clearance)
            .ThenBy(c => c.CostTier)
            .ThenBy(c => c.JoinSequence)
            .First();

        var reason = winner.Locality == AgentLocality.Local
            ? "local agent, preferred for delegation"
            : "only eligible agent in the room";

        return new ElectionResult(winner.Nick, reason);
    }

    /// <summary>
    /// Whether an agent may be trusted to read everything in a room of this sensitivity.
    /// <see cref="AgentLocality.Unknown"/> is treated as frontier and
    /// <see cref="DataSensitivity.Unknown"/> as no clearance at all — both fail closed.
    /// </summary>
    public static bool CanDelegateFor(AgentCandidate candidate, DataSensitivity roomSensitivity)
    {
        if (roomSensitivity == DataSensitivity.Unknown)
        {
            roomSensitivity = DataSensitivity.Sensitive;
        }

        // Public rooms carry nothing worth protecting, so locality does not constrain the choice.
        if (roomSensitivity > DataSensitivity.Public && candidate.Locality != AgentLocality.Local)
        {
            return false;
        }

        return candidate.Clearance != DataSensitivity.Unknown && candidate.Clearance >= roomSensitivity;
    }

    /// <summary>
    /// Whether a request of a given sensitivity may be routed to this agent. Same rule as
    /// delegation eligibility, expressed for the dispatch decision: an unclassified request is
    /// treated as sensitive, so the fallback is always a local agent.
    /// </summary>
    public static bool CanReceive(AgentCandidate candidate, DataSensitivity requestSensitivity) =>
        CanDelegateFor(candidate, requestSensitivity == DataSensitivity.Unknown
            ? DataSensitivity.Sensitive
            : requestSensitivity);
}
