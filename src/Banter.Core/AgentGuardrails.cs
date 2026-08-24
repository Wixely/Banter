namespace Banter.Core;

/// <summary>
/// Per-room limits on agent chatter (PLAN §5). Two chatty agents in one room will otherwise run
/// away with the token bill, so these are on by default rather than opt-in. Humans are never
/// throttled — the limits exist to bound machine-to-machine loops.
/// </summary>
public sealed record AgentGuardrails
{
    public static AgentGuardrails Default { get; } = new();

    /// <summary>Sliding one-minute cap on messages from agent accounts in a room.</summary>
    public int MaxAgentMessagesPerMinute { get; init; } = 20;

    /// <summary>How many agent messages may follow one another with no human message in
    /// between before the room stops relaying them. A human speaking clears the break.</summary>
    public int MaxConsecutiveAgentMessages { get; init; } = 12;

    public bool Enabled { get; init; } = true;

    /// <summary>The nick system announcements are attributed to. Reserved: account creation
    /// should refuse it.</summary>
    public const string SystemNick = "banter";
}

/// <summary>Why a room refused an agent message.</summary>
public enum GuardrailVerdict
{
    Allowed,
    RateLimited,
    LoopBroken,
}
