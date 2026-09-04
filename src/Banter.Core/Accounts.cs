namespace Banter.Core;

/// <summary>An authenticated identity. Agents are just users with <see cref="IsAgent"/> set —
/// clients render them distinctly, the server treats them alike (PLAN §1).</summary>
/// <summary>
/// A Banter account. <paramref name="IsAdmin"/> carries the oversight rule from PLAN §8a: an
/// admin is added to every room an agent opens, so no agent can hold a conversation the operator
/// cannot see.
/// </summary>
public sealed record BanterAccount(string Username, bool IsAgent, bool IsAdmin = false);

/// <summary>Credential validation seam. Phase 1 ships the in-memory store; SQLite-backed
/// accounts replace it behind this interface without touching the server.</summary>
public interface IAccountStore
{
    ValueTask<BanterAccount?> AuthenticateAsync(string username, string secret, CancellationToken cancellationToken = default);
}

/// <summary>
/// The users page's half of the store: what an operator does to accounts while the server runs.
/// Separate from <see cref="IAccountStore"/> because most of the server only ever needs to check
/// a credential — sessions get this only so the admin verbs can reach it, and a server wired
/// without it refuses those verbs rather than growing a second account system.
///
/// <para>Everything here takes usernames, never credentials, except the two password operations —
/// and those SET a password without ever being able to read one back. That asymmetry is the
/// design: the server holds hashes, so there is nothing to read.</para>
/// </summary>
public interface IAccountAdminStore
{
    /// <summary>The human accounts. Agent accounts are deliberately absent: agents live on the
    /// agents page as identities, and listing the leftovers here would suggest they belong.</summary>
    Task<IReadOnlyList<BanterAccount>> ListUsersAsync(CancellationToken cancellationToken = default);

    Task<bool> ExistsAsync(string username, CancellationToken cancellationToken = default);

    Task CreateUserAsync(string username, string secret, bool isAgent = false, bool isAdmin = false, CancellationToken cancellationToken = default);

    Task SetAdminAsync(string username, bool isAdmin, CancellationToken cancellationToken = default);

    /// <summary>Replaces the password outright — the reset path, where an admin's word is the
    /// authority. Self-service change goes through <see cref="ChangePasswordAsync"/> instead.</summary>
    Task SetPasswordAsync(string username, string secret, CancellationToken cancellationToken = default);

    /// <summary>Replaces the password only if <paramref name="oldSecret"/> is the current one.
    /// False means it was not — the caller cannot tell that apart from a race, and does not need to.</summary>
    Task<bool> ChangePasswordAsync(string username, string oldSecret, string newSecret, CancellationToken cancellationToken = default);

    Task DeleteAsync(string username, CancellationToken cancellationToken = default);

    /// <summary>How many admins remain. The guard that keeps the last operator from locking
    /// everyone out lives on this number.</summary>
    Task<int> CountAdminsAsync(CancellationToken cancellationToken = default);
}

/// <summary>Development/test account store. Secrets are compared verbatim — do not take this
/// to production; the SQLite store owns real credential hashing.</summary>
public sealed class InMemoryAccountStore : IAccountStore, IAccountAdminStore
{
    private readonly Dictionary<string, (string Secret, bool IsAgent, bool IsAdmin)> _accounts =
        new(StringComparer.OrdinalIgnoreCase);

    public InMemoryAccountStore AddUser(string username, string secret, bool isAgent = false, bool isAdmin = false)
    {
        _accounts[username] = (secret, isAgent, isAdmin);
        return this;
    }

    public ValueTask<BanterAccount?> AuthenticateAsync(string username, string secret, CancellationToken cancellationToken = default)
    {
        BanterAccount? account =
            _accounts.TryGetValue(username, out var entry) && entry.Secret == secret
                ? new BanterAccount(username, entry.IsAgent, entry.IsAdmin)
                : null;
        return ValueTask.FromResult(account);
    }

    public Task<IReadOnlyList<BanterAccount>> ListUsersAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<BanterAccount>>(
            [.. _accounts.Where(a => !a.Value.IsAgent)
                .Select(a => new BanterAccount(a.Key, false, a.Value.IsAdmin))
                .OrderBy(a => a.Username, StringComparer.OrdinalIgnoreCase)]);

    public Task<bool> ExistsAsync(string username, CancellationToken cancellationToken = default) =>
        Task.FromResult(_accounts.ContainsKey(username));

    Task IAccountAdminStore.CreateUserAsync(string username, string secret, bool isAgent, bool isAdmin, CancellationToken cancellationToken)
    {
        AddUser(username, secret, isAgent, isAdmin);
        return Task.CompletedTask;
    }

    public Task SetAdminAsync(string username, bool isAdmin, CancellationToken cancellationToken = default)
    {
        if (_accounts.TryGetValue(username, out var entry))
        {
            _accounts[username] = (entry.Secret, entry.IsAgent, isAdmin);
        }

        return Task.CompletedTask;
    }

    public Task SetPasswordAsync(string username, string secret, CancellationToken cancellationToken = default)
    {
        if (_accounts.TryGetValue(username, out var entry))
        {
            _accounts[username] = (secret, entry.IsAgent, entry.IsAdmin);
        }

        return Task.CompletedTask;
    }

    public Task<bool> ChangePasswordAsync(string username, string oldSecret, string newSecret, CancellationToken cancellationToken = default)
    {
        if (!_accounts.TryGetValue(username, out var entry) || entry.Secret != oldSecret)
        {
            return Task.FromResult(false);
        }

        _accounts[username] = (newSecret, entry.IsAgent, entry.IsAdmin);
        return Task.FromResult(true);
    }

    public Task DeleteAsync(string username, CancellationToken cancellationToken = default)
    {
        _accounts.Remove(username);
        return Task.CompletedTask;
    }

    public Task<int> CountAdminsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(_accounts.Count(a => a.Value.IsAdmin));
}

/// <summary>Room name rules, shared by server validation and client-side pre-checks.</summary>
public static class RoomName
{
    public static bool IsValid(string? name) =>
        name is { Length: >= 2 and <= 64 }
        && name[0] == '#'
        && name.Skip(1).All(c => !char.IsWhiteSpace(c) && !char.IsControl(c));
}
