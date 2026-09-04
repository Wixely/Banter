using Banter.Core;
using Dapper;

namespace Banter.Server.Persistence;

/// <summary>Database-backed <see cref="IAccountStore"/>: PBKDF2 credentials, usernames stored
/// and looked up lower-cased. Provider-agnostic — Dapper over <see cref="BanterDatabase"/>.</summary>
public sealed class DbAccountStore(BanterDatabase database) : IAccountStore, IAccountAdminStore
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
        public bool IsAdmin { get; set; }
    }

    public async ValueTask<BanterAccount?> AuthenticateAsync(
        string username, string secret, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        var row = await connection.QuerySingleOrDefaultAsync<AccountRow>(
            """
            SELECT username AS Username, password_hash AS PasswordHash, password_salt AS PasswordSalt,
                   iterations AS Iterations, is_agent AS IsAgent, is_admin AS IsAdmin
            FROM accounts WHERE username = @Username
            """,
            new { Username = Normalize(username) }).ConfigureAwait(false);

        return row is not null && PasswordHasher.Verify(secret, row.PasswordHash, row.PasswordSalt, row.Iterations)
            ? new BanterAccount(row.Username, row.IsAgent, row.IsAdmin)
            : null;
    }

    public async Task CreateUserAsync(
        string username, string secret, bool isAgent = false, bool isAdmin = false, CancellationToken cancellationToken = default)
    {
        var (hash, salt) = PasswordHasher.Hash(secret);
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(
            """
            INSERT INTO accounts (username, password_hash, password_salt, iterations, is_agent, is_admin)
            VALUES (@Username, @Hash, @Salt, @Iterations, @IsAgent, @IsAdmin)
            """,
            new
            {
                Username = Normalize(username),
                Hash = hash,
                Salt = salt,
                Iterations = PasswordHasher.DefaultIterations,
                IsAgent = isAgent,
                IsAdmin = isAdmin,
            }).ConfigureAwait(false);
    }

    /// <summary>Whether an account exists, without needing its password.</summary>
    public async Task<bool> ExistsAsync(string username, CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        return await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM accounts WHERE username = @Username", new { Username = Normalize(username) })
            .ConfigureAwait(false) > 0;
    }

    /// <summary>Set or clear the admin flag on an existing account.</summary>
    public async Task SetAdminAsync(string username, bool isAdmin, CancellationToken cancellationToken = default)
    {
        await using var connection = database.CreateConnection();
        await connection.ExecuteAsync(
            "UPDATE accounts SET is_admin = @IsAdmin WHERE username = @Username",
            new { Username = Normalize(username), IsAdmin = isAdmin }).ConfigureAwait(false);
    }

    public async Task<BanterAccount?> FindAsync(string username, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        var row = await connection.QuerySingleOrDefaultAsync<AccountRow>(
            "SELECT username AS Username, is_agent AS IsAgent, is_admin AS IsAdmin FROM accounts WHERE username = @Username",
            new { Username = Normalize(username) }).ConfigureAwait(false);
        return row is null ? null : new BanterAccount(row.Username, row.IsAgent, row.IsAdmin);
    }

    /// <summary>Human accounts only, ordered by name. Legacy agent password accounts (from before
    /// the identity system) are excluded on purpose — surfacing them on the users page would
    /// invite managing agents in two places.</summary>
    public async Task<IReadOnlyList<BanterAccount>> ListUsersAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<AccountRow>(
            """
            SELECT username AS Username, is_agent AS IsAgent, is_admin AS IsAdmin
            FROM accounts WHERE is_agent = @No ORDER BY username
            """,
            new { No = false }).ConfigureAwait(false);
        return [.. rows.Select(r => new BanterAccount(r.Username, r.IsAgent, r.IsAdmin))];
    }

    public async Task SetPasswordAsync(string username, string secret, CancellationToken cancellationToken = default)
    {
        var (hash, salt) = PasswordHasher.Hash(secret);
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(
            """
            UPDATE accounts SET password_hash = @Hash, password_salt = @Salt, iterations = @Iterations
            WHERE username = @Username
            """,
            new { Username = Normalize(username), Hash = hash, Salt = salt, Iterations = PasswordHasher.DefaultIterations })
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The self-service path: the old password is the proof of identity, verified against the
    /// stored hash exactly as sign-in verifies it. Read-verify-write rather than one guarded
    /// UPDATE because the hash comparison cannot happen in SQL — the salt is per-account.
    /// </summary>
    public async Task<bool> ChangePasswordAsync(string username, string oldSecret, string newSecret, CancellationToken cancellationToken = default)
    {
        if (await AuthenticateAsync(username, oldSecret, cancellationToken).ConfigureAwait(false) is null)
        {
            return false;
        }

        await SetPasswordAsync(username, newSecret, cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task DeleteAsync(string username, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(
            "DELETE FROM accounts WHERE username = @Username",
            new { Username = Normalize(username) }).ConfigureAwait(false);
    }

    public async Task<int> CountAdminsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM accounts WHERE is_admin = @Yes", new { Yes = true }).ConfigureAwait(false);
    }

    public async Task<int> CountAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM accounts").ConfigureAwait(false);
    }

    private static string Normalize(string username) => username.Trim().ToLowerInvariant();
}
