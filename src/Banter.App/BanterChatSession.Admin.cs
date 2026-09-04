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

    public async Task CreateAgentIdentityAsync(
        string nick,
        IReadOnlyList<string> rooms,
        IReadOnlyList<string> skills,
        AgentLocality locality,
        DataSensitivity clearance)
    {
        try
        {
            var created = await _client.CreateAgentAsync(nick, rooms, skills, locality, clearance).ConfigureAwait(false);

            // The code is shown before the list is refreshed: the refresh is housekeeping, and the
            // code is the one thing here that exists for a moment and then never again.
            _vm.Post(() =>
            {
                _vm.ShowEnrolmentCode(created.Nick, created.Code);
                _vm.ClearNewAgent();
                _vm.SelectAdminAgent(created.Nick);
            });

            await LoadAgentIdentitiesAsync().ConfigureAwait(false);
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
            _vm.Post(() =>
            {
                _vm.ShowTempPassword(created.Username, created.Password);
                _vm.ClearNewUser();
            });

            await LoadUsersAsync().ConfigureAwait(false);
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
                _vm.SelectAdminUser("");
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
                _vm.SelectAdminAgent("");
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
