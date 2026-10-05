using Dapper;

namespace Banter.Server.Persistence;

/// <summary>Who an agent is on a hub that checks: the user it was issued, and the key it holds.</summary>
/// <param name="UserId">The hub's own id for that user, which is what its grants are keyed by.</param>
/// <param name="Token">The key, as issued. A hub shows one once, so this is the only copy.</param>
/// <param name="IssuedAtUtc">When it was issued, so a rotation is visible without asking the hub.</param>
public sealed record HubIdentity(string UserId, string Token, DateTimeOffset IssuedAtUtc);

/// <summary>
/// Which hub user each agent is.
///
/// <para>An MCP server that checks identity decides what a caller may use from the key it presents.
/// So an agent needs a key of its own there, and this is where Banter remembers which one. Calls
/// for that agent are then made <em>as</em> that agent, and the hub refuses anything it did not
/// grant rather than trusting this server to have filtered first. An agent that went around Banter
/// and called the hub directly with the same key would get exactly the same answer, which is the
/// point: Banter manages the policy, the hub enforces it.</para>
///
/// <para>Keyed by upstream as well as agent: two hubs issue two different keys to the same agent,
/// and one row could only ever be right about one of them.</para>
/// </summary>
public sealed class HubIdentityStore(BanterDatabase database)
{
    /// <summary>The agent's identity on that hub, or null when it has never been issued one.</summary>
    public async Task<HubIdentity?> ForAgentAsync(
        string upstream, string agent, CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        var row = await connection.QuerySingleOrDefaultAsync<HubIdentityRow?>(
            "SELECT user_id AS UserId, token AS Token, issued_at AS IssuedAt FROM hub_identities "
            + "WHERE upstream = @upstream AND agent = @agent",
            new { upstream, agent }).ConfigureAwait(false);

        return row is { } found
            ? new HubIdentity(found.UserId, found.Token, DateTimeOffset.FromUnixTimeMilliseconds(found.IssuedAt))
            : null;
    }

    /// <summary>
    /// Records the identity an agent was issued, replacing any it had.
    ///
    /// <para>Replacing is what a rotation is: the hub retires the old key the moment it issues a
    /// new one, so keeping the previous row would only preserve a key that no longer works.</para>
    /// </summary>
    public async Task SaveAsync(
        string upstream,
        string agent,
        HubIdentity identity,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(upstream);
        ArgumentException.ThrowIfNullOrWhiteSpace(agent);
        ArgumentNullException.ThrowIfNull(identity);

        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await connection.ExecuteAsync(
            "DELETE FROM hub_identities WHERE upstream = @upstream AND agent = @agent",
            new { upstream, agent }, transaction).ConfigureAwait(false);

        await connection.ExecuteAsync(
            "INSERT INTO hub_identities (upstream, agent, user_id, token, issued_at) "
            + "VALUES (@upstream, @agent, @UserId, @Token, @IssuedAt)",
            new
            {
                upstream,
                agent,
                identity.UserId,
                identity.Token,

                // Unix milliseconds in an integer column, as the task ledger stores its times: one
                // shape that means the same thing in both dialects and needs no type handler.
                IssuedAt = identity.IssuedAtUtc.ToUnixTimeMilliseconds(),
            },
            transaction).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Forgets an agent's identity here. The user itself is the hub's to delete — this only stops
    /// Banter holding a key for it, which is what an operator means by "this agent is not mine any
    /// more" rather than "retire that key everywhere".
    /// </summary>
    public async Task RemoveAsync(string upstream, string agent, CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        await connection.ExecuteAsync(
            "DELETE FROM hub_identities WHERE upstream = @upstream AND agent = @agent",
            new { upstream, agent }).ConfigureAwait(false);
    }

    /// <summary>Every agent with an identity on that hub, for the management view. No keys.</summary>
    public async Task<IReadOnlyDictionary<string, string>> AgentsAsync(
        string upstream, CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        var rows = await connection.QueryAsync<(string Agent, string UserId)>(
            "SELECT agent AS Agent, user_id AS UserId FROM hub_identities WHERE upstream = @upstream ORDER BY agent",
            new { upstream }).ConfigureAwait(false);

        return rows.ToDictionary(r => r.Agent, r => r.UserId, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Dapper needs a named shape to map into; tuples do not bind by column name here.</summary>
    private sealed record HubIdentityRow(string UserId, string Token, long IssuedAt);
}
