using System.Text.Json;
using Banter.Server.Persistence;

namespace Banter.Server.Tools;

/// <summary>
/// Giving an agent an identity on the hub, and telling the hub what that identity may use.
///
/// <para>Banter manages; the hub decides. Every call here is a management tool on the hub —
/// <c>users__create</c>, <c>permissions__set_grants</c> — so the answer to "what may this agent
/// use" lives in one place and is enforced there, at the moment a tool runs, against the key the
/// caller presented. An agent that went around Banter and called the hub directly with its own key
/// gets the same answer, because it is the same answer.</para>
///
/// <para>The alternative — Banter keeping its own list and filtering what it forwards — makes this
/// server's correctness part of the security boundary, and silently stops being true the moment
/// anything talks to the hub without going through here.</para>
/// </summary>
public sealed class HubProvisioner(IHubAdmin hub, HubIdentityStore identities, string upstreamKey)
{
    /// <summary>
    /// The agent's identity on the hub, minting one the first time.
    ///
    /// <para>Minting is <c>users__create</c>, whose key is shown once and stored here — a second
    /// call would issue a second user rather than return the first, so an identity we already hold
    /// is never re-created.</para>
    /// </summary>
    public async Task<HubIdentity> EnsureAsync(string agent, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agent);

        if (await identities.ForAgentAsync(upstreamKey, agent, cancellationToken).ConfigureAwait(false) is { } existing)
        {
            return existing;
        }

        var issued = await hub.CallAsync(
            "users__create",
            new Dictionary<string, object?> { ["name"] = agent },
            cancellationToken).ConfigureAwait(false);

        var identity = Read(issued, agent);
        await identities.SaveAsync(upstreamKey, agent, identity, cancellationToken).ConfigureAwait(false);
        return identity;
    }

    /// <summary>
    /// Issues the agent a new key, retiring the one it had.
    ///
    /// <para>For a key that has leaked, or an agent that has been handed to somebody else. The hub
    /// retires the old one the moment it issues the new, so this is also the only honest way to cut
    /// off an agent that still holds a copy — short of suspending it there.</para>
    /// </summary>
    public async Task<HubIdentity> RotateAsync(string agent, CancellationToken cancellationToken = default)
    {
        var current = await EnsureAsync(agent, cancellationToken).ConfigureAwait(false);

        var issued = await hub.CallAsync(
            "users__rotate_key",
            new Dictionary<string, object?> { ["user"] = current.UserId },
            cancellationToken).ConfigureAwait(false);

        var identity = Read(issued, agent) with { UserId = current.UserId };
        await identities.SaveAsync(upstreamKey, agent, identity, cancellationToken).ConfigureAwait(false);
        return identity;
    }

    /// <summary>
    /// What the hub says this agent may use. Read from the hub rather than from anything here: a
    /// local copy would be a second answer to a question with one authority, and would drift the
    /// moment somebody changed it in the hub's own UI.
    /// </summary>
    public async Task<IReadOnlyList<string>> GrantsAsync(
        string agent, CancellationToken cancellationToken = default)
    {
        if (await identities.ForAgentAsync(upstreamKey, agent, cancellationToken).ConfigureAwait(false)
            is not { } identity)
        {
            return [];
        }

        var listed = await hub.CallAsync("permissions__list_grants", null, cancellationToken).ConfigureAwait(false);
        if (!listed.TryGetProperty("grants", out var grants) || grants.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        foreach (var grant in grants.EnumerateArray())
        {
            if (grant.TryGetProperty("userId", out var userId)
                && string.Equals(userId.GetString(), identity.UserId, StringComparison.Ordinal)
                && grant.TryGetProperty("tools", out var tools)
                && tools.ValueKind == JsonValueKind.Array)
            {
                return [.. tools.EnumerateArray().Select(t => t.GetString()).OfType<string>()];
            }
        }

        return [];
    }

    /// <summary>
    /// Replaces what the agent may use, on the hub. Minting its identity first if it has none:
    /// granting tools to an agent that cannot yet authenticate is a grant nobody holds.
    /// </summary>
    public async Task SetGrantsAsync(
        string agent, IReadOnlyList<string> tools, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tools);
        var identity = await EnsureAsync(agent, cancellationToken).ConfigureAwait(false);

        await hub.CallAsync(
            "permissions__set_grants",
            new Dictionary<string, object?> { ["user"] = identity.UserId, ["tools"] = tools },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Removes the agent from the hub and forgets its key here.
    ///
    /// <para>Deleted there as well as forgotten here, because a user left behind holds a key this
    /// server no longer tracks — and the hub drops its grants with it, so nothing is left naming an
    /// id nobody holds.</para>
    /// </summary>
    public async Task RemoveAsync(string agent, CancellationToken cancellationToken = default)
    {
        if (await identities.ForAgentAsync(upstreamKey, agent, cancellationToken).ConfigureAwait(false)
            is not { } identity)
        {
            return;
        }

        try
        {
            await hub.CallAsync(
                "users__delete",
                new Dictionary<string, object?> { ["user"] = identity.UserId },
                cancellationToken).ConfigureAwait(false);
        }
        catch (HubAdminException ex) when (ex.Code is "users.no_such_user")
        {
            // Already gone there. Forgetting it here is still the right end state.
        }

        await identities.RemoveAsync(upstreamKey, agent, cancellationToken).ConfigureAwait(false);
    }

    private static HubIdentity Read(JsonElement issued, string agent)
    {
        var key = issued.TryGetProperty("key", out var k) ? k.GetString() : null;
        var userId = issued.TryGetProperty("user", out var user)
                     && user.TryGetProperty("id", out var id)
            ? id.GetString()
            : null;

        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(userId))
        {
            // The key is shown once. If this reply is not the shape we expect, the key in it is
            // already lost, so the only honest thing is to fail rather than store half of it.
            throw new HubAdminException(
                "hub.unreadable", $"The hub issued '{agent}' a key in a shape this server could not read.");
        }

        return new HubIdentity(userId, key, DateTimeOffset.UtcNow);
    }
}
