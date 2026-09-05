using Banter.Client.Core;
using Banter.Protocol;

namespace Banter.App;

/// <summary>
/// The agents page talking to the server. Every one of these is admin-only server-side, so a
/// refusal is reported rather than guessed at locally — the client does not get to decide who is
/// an operator.
/// </summary>
public sealed partial class BanterChatSession
{
    public async Task LoadAgentIdentitiesAsync()
    {
        try
        {
            var identities = await _client.ListAgentsAsync().ConfigureAwait(false);
            _vm.Post(() => _vm.SetAgentIdentities(identities));
        }
        catch (BanterErrorException ex)
        {
            _vm.Post(() => _vm.AdminFailed(ex.Message));
        }
    }

    public async Task CreateAgentIdentityAsync(AgentForm form)
    {
        try
        {
            var created = await _client.CreateAgentAsync(
                form.Nick, form.Rooms, form.Skills, form.Locality, form.Clearance,
                form.CostTier, form.WantsDelegator).ConfigureAwait(false);

            // The code is shown before the list is refreshed: the refresh is housekeeping, and the
            // code is the one thing here that exists for a moment and then never again.
            _vm.Post(() => _vm.ShowEnrolmentCode(created.Nick, created.Code));
            await LoadAgentIdentitiesAsync().ConfigureAwait(false);

            // Land on the thing just made, so the code and its subject are on screen together.
            _vm.Post(() => _vm.SelectAdminAgent(created.Nick));
        }
        catch (BanterErrorException ex)
        {
            _vm.Post(() => _vm.AdminFailed(ex.Message));
        }
    }

    /// <summary>
    /// Saves an existing agent. Cost and the delegator wish are absolute state here, not a diff:
    /// the form shows what they are, so an empty cost box means "clear the override" rather than
    /// "leave whatever was there".
    /// </summary>
    public async Task SaveAgentIdentityAsync(AgentForm form)
    {
        try
        {
            await _client.UpdateAgentAsync(
                form.Nick,
                rooms: form.Rooms,
                skills: form.Skills,
                locality: form.Locality,
                clearance: form.Clearance,
                costTier: form.CostTier, clearCostTier: form.CostTier is null,
                wantsDelegator: form.WantsDelegator, clearWantsDelegator: form.WantsDelegator is null)
                .ConfigureAwait(false);

            await LoadAgentIdentitiesAsync().ConfigureAwait(false);
            _vm.Post(() => _vm.SelectAdminAgent(form.Nick));
        }
        catch (BanterErrorException ex)
        {
            _vm.Post(() => _vm.AdminFailed(ex.Message));
        }
    }

    public async Task ReissueAgentIdentityAsync(string nick)
    {
        try
        {
            var reissued = await _client.ReissueAgentAsync(nick).ConfigureAwait(false);
            _vm.Post(() => _vm.ShowEnrolmentCode(reissued.Nick, reissued.Code));
            await LoadAgentIdentitiesAsync().ConfigureAwait(false);
        }
        catch (BanterErrorException ex)
        {
            _vm.Post(() => _vm.AdminFailed(ex.Message));
        }
    }

    // ---- The users tab ----

    public async Task LoadUsersAsync()
    {
        try
        {
            var users = await _client.ListUsersAsync().ConfigureAwait(false);
            _vm.Post(() => _vm.SetUsers(users));
        }
        catch (BanterErrorException ex)
        {
            _vm.Post(() => _vm.AdminFailed(ex.Message));
        }
    }

    public async Task CreateUserAccountAsync(string username, bool isAdmin)
    {
        try
        {
            var created = await _client.CreateUserAsync(username, isAdmin).ConfigureAwait(false);

            // The password is shown before the list is refreshed, for the same reason the
            // enrolment code is: the refresh is housekeeping, and this is the only moment the
            // password exists anywhere an operator can read it.
            _vm.Post(() => _vm.ShowTempPassword(created.Username, created.Password));
            await LoadUsersAsync().ConfigureAwait(false);
            _vm.Post(() => _vm.SelectAdminUser(created.Username));
        }
        catch (BanterErrorException ex)
        {
            _vm.Post(() => _vm.AdminFailed(ex.Message));
        }
    }

    public async Task ResetUserPasswordAsync(string username)
    {
        try
        {
            var reset = await _client.ResetUserPasswordAsync(username).ConfigureAwait(false);
            _vm.Post(() => _vm.ShowTempPassword(reset.Username, reset.Password));
        }
        catch (BanterErrorException ex)
        {
            _vm.Post(() => _vm.AdminFailed(ex.Message));
        }
    }

    public async Task SetUserAdminAsync(string username, bool isAdmin)
    {
        try
        {
            await _client.SetUserAdminAsync(username, isAdmin).ConfigureAwait(false);
            await LoadUsersAsync().ConfigureAwait(false);
            _vm.Post(() => _vm.SelectAdminUser(username));
        }
        catch (BanterErrorException ex)
        {
            _vm.Post(() => _vm.AdminFailed(ex.Message));
        }
    }

    public async Task RemoveUserAccountAsync(string username)
    {
        try
        {
            await _client.DeleteUserAsync(username).ConfigureAwait(false);
            _vm.Post(() =>
            {
                _vm.ClearUserDetail();
                _vm.ClearAdminCode();
            });

            await LoadUsersAsync().ConfigureAwait(false);
        }
        catch (BanterErrorException ex)
        {
            _vm.Post(() => _vm.AdminFailed(ex.Message));
        }
    }

    public async Task RemoveAgentIdentityAsync(string nick)
    {
        try
        {
            await _client.DeleteAgentAsync(nick).ConfigureAwait(false);
            _vm.Post(() =>
            {
                _vm.ClearAgentDetail();
                _vm.ClearAdminCode();
            });

            await LoadAgentIdentitiesAsync().ConfigureAwait(false);
        }
        catch (BanterErrorException ex)
        {
            _vm.Post(() => _vm.AdminFailed(ex.Message));
        }
    }
}
