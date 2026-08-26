using Banter.Client.Core;

namespace Banter.App;

/// <summary>
/// The client's half of tool management (PLAN §8). The client never calls a tool — it only reads
/// the catalogue and writes grants, and the server refuses both unless the account is an admin.
/// This is the management surface the server will eventually serve to a browser as WASM, which is
/// why it lives in the shared app rather than in the desktop head.
/// </summary>
public sealed partial class BanterChatSession
{
    /// <summary>
    /// Fill the grants panel: the whole catalogue, plus the named agent's grants if one is
    /// selected. A non-admin gets NOT_ADMIN or an empty catalogue, and is told so plainly rather
    /// than left looking at a blank panel.
    /// </summary>
    public async Task LoadToolsAsync(string agent)
    {
        try
        {
            var catalogue = await _client.ListToolsAsync().ConfigureAwait(false);
            _vm.Post(() =>
            {
                _vm.SetToolCatalogue(catalogue.Tools.Select(t => (t.Name, t.ServerKey, t.Description)));
                if (catalogue.Tools.Count == 0)
                {
                    _vm.ToolGrantsFailed("This server has no tools connected.");
                }
            });
        }
        catch (BanterErrorException ex)
        {
            _vm.Post(() => _vm.ToolGrantsFailed(ex.Message));
            return;
        }

        if (agent.Length == 0)
        {
            return;
        }

        try
        {
            var grants = await _client.GetToolGrantsAsync(agent).ConfigureAwait(false);
            _vm.Post(() => _vm.SetToolGrants(agent, grants.Tools));
        }
        catch (BanterErrorException ex)
        {
            _vm.Post(() => _vm.ToolGrantsFailed(ex.Message));
        }
    }

    /// <summary>
    /// Write an agent's grants. The server answers with what it actually stored, which is not
    /// always what was asked for — it drops names nothing serves — so the panel adopts the reply
    /// rather than the request.
    /// </summary>
    public async Task SaveToolsAsync(string agent, IReadOnlyList<string> tools)
    {
        try
        {
            var stored = await _client.SetToolGrantsAsync(agent, tools).ConfigureAwait(false);
            _vm.Post(() => _vm.ToolGrantsSaved(agent, stored.Tools));
        }
        catch (BanterErrorException ex)
        {
            _vm.Post(() => _vm.ToolGrantsFailed(ex.Message));
        }
    }
}
