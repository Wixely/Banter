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
