using Banter.Core;
using Dapper;

namespace Banter.Server.Persistence;

/// <summary>Database-backed <see cref="IAccountStore"/>: PBKDF2 credentials, usernames stored
/// and looked up lower-cased. Provider-agnostic — Dapper over <see cref="BanterDatabase"/>.</summary>
public sealed class DbAccountStore(BanterDatabase database) : IAccountStore
{
    // A mutable class, not a record: Dapper's property mapping converts provider-specific
    // numerics (SQLite INTEGER=long, PostgreSQL INTEGER/BOOLEAN) where constructor mapping
    // demands exact types.
    private sealed class AccountRow
    {
        public string Username { get; set; } = "";
        public byte[] PasswordHash { get; set; } = [];
        public byte[] PasswordSalt { get; set; } = [];
        public int Iterations { get; set; }
        public bool IsAgent { get; set; }
    }

    public async ValueTask<BanterAccount?> AuthenticateAsync(
        string username, string secret, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        var row = await connection.QuerySingleOrDefaultAsync<AccountRow>(
            """
            SELECT username AS Username, password_hash AS PasswordHash, password_salt AS PasswordSalt,
                   iterations AS Iterations, is_agent AS IsAgent
            FROM accounts WHERE username = @Username
            """,
            new { Username = Normalize(username) }).ConfigureAwait(false);

        return row is not null && PasswordHasher.Verify(secret, row.PasswordHash, row.PasswordSalt, row.Iterations)
            ? new BanterAccount(row.Username, row.IsAgent)
            : null;
    }

    public async Task CreateUserAsync(
        string username, string secret, bool isAgent = false, CancellationToken cancellationToken = default)
    {
        var (hash, salt) = PasswordHasher.Hash(secret);
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(
            """
            INSERT INTO accounts (username, password_hash, password_salt, iterations, is_agent)
            VALUES (@Username, @Hash, @Salt, @Iterations, @IsAgent)
            """,
            new
            {
                Username = Normalize(username),
                Hash = hash,
                Salt = salt,
                Iterations = PasswordHasher.DefaultIterations,
                IsAgent = isAgent,
            }).ConfigureAwait(false);
    }

    public async Task<int> CountAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM accounts").ConfigureAwait(false);
    }

    private static string Normalize(string username) => username.Trim().ToLowerInvariant();
}
