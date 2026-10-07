using Banter.Protocol;

namespace Banter.App;

/// <summary>
/// Everything the four admin pages - agents, users, work and MCPHub - need of a session.
///
/// <para>One property on the app rather than fourteen, because fourteen is a list a head can be
/// PART of the way down. That is not hypothetical: the Android and web heads wired none of them
/// and had four rail buttons opening four pages that did nothing, for as long as those pages have
/// existed, and nothing anywhere said so - an admin page with no hook and an admin page waiting on
/// a slow server look identical. With one property a head either wires the admin pages or it
/// visibly does not.</para>
///
/// <para>Still delegates rather than the session itself, so a test can hand over exactly the one
/// it wants to watch: <c>AdminHooks.None with { AgentRemoveAsync = … }</c>.</para>
/// </summary>
public sealed record AdminHooks
{
    /// <summary>A head that does no administration. The pages are still reachable for an admin,
    /// and still do nothing - which is why <see cref="SetIsAdmin"/> on the view-model is what
    /// decides whether the buttons appear at all.</summary>
    public static AdminHooks None { get; } = new();

    /// <summary>The agents page (admin only).</summary>
    public Func<Task> AgentsListAsync { get; init; } = () => Task.CompletedTask;

    /// <summary>Creating an agent; the reply's one-time code is the host's to show.</summary>
    public Func<AgentForm, Task> AgentCreateAsync { get; init; } = _ => Task.CompletedTask;

    /// <summary>Saving an existing agent. Same form as the create, because a create is a save of
    /// something that did not exist yet.</summary>
    public Func<AgentForm, Task> AgentSaveAsync { get; init; } = _ => Task.CompletedTask;

    /// <summary>A fresh code, retiring the key currently enrolled for this agent.</summary>
    public Func<string, Task> AgentReissueAsync { get; init; } = _ => Task.CompletedTask;

    /// <summary>Removes an identity. Its key stops working at once.</summary>
    public Func<string, Task> AgentRemoveAsync { get; init; } = _ => Task.CompletedTask;

    /// <summary>Every human account this server knows.</summary>
    public Func<Task> UsersListAsync { get; init; } = () => Task.CompletedTask;

    /// <summary>(username, isAdmin) - the reply's temporary password is the host's to show.</summary>
    public Func<string, bool, Task> UserCreateAsync { get; init; } = (_, _) => Task.CompletedTask;

    /// <summary>A fresh temporary password for somebody locked out.</summary>
    public Func<string, Task> UserResetAsync { get; init; } = _ => Task.CompletedTask;

    /// <summary>Grants or revokes admin.</summary>
    public Func<string, bool, Task> UserSetAdminAsync { get; init; } = (_, _) => Task.CompletedTask;

    /// <summary>Removes an account. Their password stops working at once.</summary>
    public Func<string, Task> UserRemoveAsync { get; init; } = _ => Task.CompletedTask;

    /// <summary>Reads every room's work. Admin-only on the server.</summary>
    public Func<Task> WorkListAsync { get; init; } = () => Task.CompletedTask;

    /// <summary>
    /// Asks the server about its tool hubs. Admin-only there, and the page does not edit grants:
    /// MCPHub owns those.
    /// </summary>
    public Func<Task> HubsListAsync { get; init; } = () => Task.CompletedTask;

    /// <summary>(hub, agent) - a new key for this agent there, retiring the one it holds.</summary>
    public Func<string, string, Task> HubRotateAsync { get; init; } = (_, _) => Task.CompletedTask;

    /// <summary>(hub, agent) - remove this agent's identity there entirely.</summary>
    public Func<string, string, Task> HubForgetAsync { get; init; } = (_, _) => Task.CompletedTask;

    /// <summary>
    /// All of them, pointed at whatever session the head is holding at the time.
    ///
    /// <para>A function rather than the session itself because a head's session is replaced on
    /// every sign-in, and a set of delegates closed over the one that existed when the app was
    /// built would go on talking to a connection nobody has any more.</para>
    /// </summary>
    public static AdminHooks For(Func<BanterChatSession?> session) => new()
    {
        AgentsListAsync = () => session()?.LoadAgentIdentitiesAsync() ?? Task.CompletedTask,
        AgentCreateAsync = form => session()?.CreateAgentIdentityAsync(form) ?? Task.CompletedTask,
        AgentSaveAsync = form => session()?.SaveAgentIdentityAsync(form) ?? Task.CompletedTask,
        AgentReissueAsync = nick => session()?.ReissueAgentIdentityAsync(nick) ?? Task.CompletedTask,
        AgentRemoveAsync = nick => session()?.RemoveAgentIdentityAsync(nick) ?? Task.CompletedTask,
        UsersListAsync = () => session()?.LoadUsersAsync() ?? Task.CompletedTask,
        UserCreateAsync = (name, admin) => session()?.CreateUserAccountAsync(name, admin) ?? Task.CompletedTask,
        UserResetAsync = name => session()?.ResetUserPasswordAsync(name) ?? Task.CompletedTask,
        UserSetAdminAsync = (name, admin) => session()?.SetUserAdminAsync(name, admin) ?? Task.CompletedTask,
        UserRemoveAsync = name => session()?.RemoveUserAccountAsync(name) ?? Task.CompletedTask,
        WorkListAsync = () => session()?.LoadAllTasksAsync() ?? Task.CompletedTask,
        HubsListAsync = () => session()?.LoadHubsAsync() ?? Task.CompletedTask,
        HubRotateAsync = (hub, agent) => session()?.RotateHubKeyAsync(hub, agent) ?? Task.CompletedTask,
        HubForgetAsync = (hub, agent) => session()?.ForgetHubIdentityAsync(hub, agent) ?? Task.CompletedTask,
    };
}
