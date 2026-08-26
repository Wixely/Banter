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

/// <summary>Development/test account store. Secrets are compared verbatim — do not take this
/// to production; the SQLite store owns real credential hashing.</summary>
public sealed class InMemoryAccountStore : IAccountStore
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
}

/// <summary>Room name rules, shared by server validation and client-side pre-checks.</summary>
public static class RoomName
{
    public static bool IsValid(string? name) =>
        name is { Length: >= 2 and <= 64 }
        && name[0] == '#'
        && name.Skip(1).All(c => !char.IsWhiteSpace(c) && !char.IsControl(c));
}
