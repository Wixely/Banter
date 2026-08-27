using Android.App;
using Android.Content.PM;
using Banter.App;
using Banter.Client.Core;
using Banter.Protocol.Transport;
using CupriFace;
using CupriFace.Android;

namespace Banter.App.Android;

/// <summary>
/// The Android head. Deliberately thin, for the same reason the desktop head is: everything
/// visible is the shared <see cref="BanterChatApp"/>, and this only supplies what the platform
/// owns — where settings live, and how a connection is made.
///
/// <para>The one real difference from the desktop head is that there is no command line to be
/// given a server and an account on, so the app starts on the connect screen and the session is
/// built once the user has filled it in.</para>
/// </summary>
[Activity(
    Label = "Banter",
    MainLauncher = true,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode
        | ConfigChanges.KeyboardHidden)]
public sealed class MainActivity : CupriActivity
{
    private const string LogTag = "Banter";

    private readonly ChatViewModel _viewModel = new();
    private BanterClient? _client;
    private BanterChatSession? _session;
    private BanterSettings _settings = new();

    protected override CupriApp CreateApp()
    {
        _settings = BanterSettings.Load(problem: p => global::Android.Util.Log.Warn(LogTag, $"settings: {p}"));

        _viewModel.SetStatus("Not connected", connected: false);
        _viewModel.ShowConnect(_settings.Server, _settings.User);

        return new BanterChatApp(_viewModel)
        {
            ConnectAsync = ConnectAsync,
            SendAsync = (room, text) => _session?.SendAsync(room, text) ?? Task.CompletedTask,
            CommandAsync = (room, line) => _session?.CommandAsync(room, line) ?? Task.CompletedTask,
            LoadOlderAsync = room => _session?.LoadOlderAsync(room, _settings.HistoryPageSize) ?? Task.CompletedTask,
            DownloadAsync = fileId => _session?.DownloadAsync(fileId) ?? Task.CompletedTask,
            JoinRoomAsync = room => _session?.JoinAsync(room, _settings.HistoryPageSize) ?? Task.CompletedTask,
            ToolsOpenAsync = agent => _session?.LoadToolsAsync(agent) ?? Task.CompletedTask,
            ToolsSaveAsync = (agent, tools) => _session?.SaveToolsAsync(agent, tools) ?? Task.CompletedTask,

            // No tray and no window to close on a phone; the OS owns that.
            StayInTray = false,
        };
    }

    /// <summary>
    /// Builds the session from what the connect screen was given. Every failure is reported back
    /// onto that screen rather than thrown — the user is standing in front of the form that caused
    /// it, and it is the only place they can do anything about it.
    /// </summary>
    private async Task ConnectAsync(string server, string user, string password)
    {
        try
        {
            if (!Uri.TryCreate(server, UriKind.Absolute, out var uri))
            {
                _viewModel.Post(() => _viewModel.ConnectFailed($"'{server}' is not a server address."));
                return;
            }

            if (uri.Scheme != "tcp")
            {
                // CupriNet on Android is a Phase 0 spike the plan still lists as outstanding
                // (§10: .NET AOT and background sockets). Saying so beats a timeout with no reason.
                _viewModel.Post(() => _viewModel.ConnectFailed(
                    $"This head speaks tcp:// only for now; '{uri.Scheme}://' is not wired yet."));
                return;
            }

            _client = await BanterClient
                .ConnectAsync(new TcpBanterTransport(), uri, user, password)
                .ConfigureAwait(false);

            _session = new BanterChatSession(_client, _viewModel);

            _viewModel.Post(() =>
            {
                _viewModel.SetNick(_client.Nick);
                _viewModel.SetStatus("Connected", connected: true);
                _viewModel.Connected();
            });

            // Remembered only once it worked, and without the password — the settings file is
            // plain JSON in the app's storage and is not a credential store.
            _settings = _settings with { Server = server, User = user };
            _settings.TrySave(problem: p => global::Android.Util.Log.Warn(LogTag, $"settings: {p}"));

            var rooms = _settings.Rooms.Count > 0 ? _settings.Rooms : ["#main"];
            foreach (var room in rooms)
            {
                try
                {
                    await _session.JoinAsync(room, _settings.HistoryPageSize).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _viewModel.Post(() => _viewModel.System(room, $"could not join {room}: {ex.Message}"));
                }
            }
        }
        catch (BanterAuthException ex)
        {
            _viewModel.Post(() => _viewModel.ConnectFailed($"Refused: {ex.Message}"));
        }
        catch (Exception ex)
        {
            _viewModel.Post(() => _viewModel.ConnectFailed(ex.Message));
        }
    }

    protected override void OnDestroy()
    {
        _session?.Dispose();
        _client?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        base.OnDestroy();
    }
}
