namespace Banter.Core;

/// <summary>
/// Bounds on the work ledger (PLAN §8b).
/// </summary>
public sealed record TaskLimits
{
    public static TaskLimits Default { get; } = new();

    /// <summary>
    /// How long a claim holds before the server reclaims it. A held task with no progress is
    /// indistinguishable from a crashed agent, so the lease is what stops work disappearing into
    /// one. A <c>TASK_UPDATE</c> renews it.
    /// </summary>
    public int DefaultLeaseSeconds { get; init; } = 1800;

    /// <summary>Live tasks one agent may hold, so a greedy agent cannot corner the board.</summary>
    public int MaxConcurrentPerAgent { get; init; } = 1;

    /// <summary>How often the server looks for lapsed leases.</summary>
    public TimeSpan SweepInterval { get; init; } = TimeSpan.FromSeconds(30);
}
