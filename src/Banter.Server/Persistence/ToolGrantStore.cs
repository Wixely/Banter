using Dapper;

namespace Banter.Server.Persistence;

/// <summary>
/// Which tools each agent may use. Persisted because a grant is an operator decision, not
/// session state — restarting the server must not quietly re-open or close what an agent can do.
/// </summary>
public sealed class ToolGrantStore(BanterDatabase database)
{
    /// <summary>
    /// Grants for an agent. Empty means the agent gets nothing: a new agent with no configured
    /// grants must not inherit access to every tool the server happens to have connected.
    /// </summary>
    public async Task<IReadOnlyList<string>> ForAgentAsync(string agent, CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        var rows = await connection.QueryAsync<string>(
            "SELECT tool FROM tool_grants WHERE agent = @agent ORDER BY tool", new { agent }).ConfigureAwait(false);
        return rows.ToList();
    }

    /// <summary>Every grant, for the management UI.</summary>
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> AllAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        var rows = await connection.QueryAsync<(string Agent, string Tool)>(
            "SELECT agent AS Agent, tool AS Tool FROM tool_grants ORDER BY agent, tool").ConfigureAwait(false);

        return rows.GroupBy(r => r.Agent, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<string>)g.Select(r => r.Tool).ToList(),
                StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Replace an agent's grants wholesale. Empty revokes everything.</summary>
    public async Task ReplaceAsync(
        string agent, IReadOnlyList<string> tools, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // Replace rather than merge, in one transaction: a revoke that half-applied would leave
        // an agent holding access somebody believed they had removed.
        await connection.ExecuteAsync(
            "DELETE FROM tool_grants WHERE agent = @agent", new { agent }, transaction).ConfigureAwait(false);

        foreach (var tool in tools.Distinct(StringComparer.Ordinal))
        {
            await connection.ExecuteAsync(
                "INSERT INTO tool_grants (agent, tool) VALUES (@agent, @tool)",
                new { agent, tool }, transaction).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }
}
