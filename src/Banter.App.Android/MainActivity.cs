using Android;
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
    private AndroidVoice? _voice;

    /// <summary>The microphone request in flight, completed by <see cref="OnRequestPermissionsResult"/>.</summary>
    private TaskCompletionSource<bool>? _micRequest;

    private const int MicRequestCode = 4101;

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
            VoiceToggleAsync = SetVoiceOpenAsync,
            ReadbackChangedAsync = policy => _session?.SetReadbackAsync(policy) ?? Task.CompletedTask,

            // No tray and no window to close on a phone; the OS owns that.
            StayInTray = false,
        };
    }

    /// <summary>
    /// Opens or closes the microphone, asking for permission the first time it is actually wanted.
    ///
    /// <para>In context rather than at launch: a chat client that demands the microphone before
    /// the user has done anything is one people refuse, and a refusal is sticky.</para>
    /// </summary>
    private async Task SetVoiceOpenAsync(bool open)
    {
        if (!open)
        {
            await (_session?.SetVoiceOpenAsync(false) ?? Task.CompletedTask).ConfigureAwait(false);
            return;
        }

        if (!await EnsureMicrophoneAsync().ConfigureAwait(false))
        {
            _viewModel.Post(() =>
            {
                _viewModel.SetVoiceState(Banter.Voice.VoiceSessionState.Idle);
                _viewModel.VoiceFailed("microphone permission was refused.");
            });
            return;
        }

        await (_session?.SetVoiceOpenAsync(true) ?? Task.CompletedTask).ConfigureAwait(false);
    }

    private Task<bool> EnsureMicrophoneAsync()
    {
        if (CheckSelfPermission(Manifest.Permission.RecordAudio) == Permission.Granted)
        {
            return Task.FromResult(true);
        }

        // One request at a time: a second tap while the dialog is up must wait on the same answer
        // rather than raise a second dialog.
        var pending = _micRequest;
        if (pending is not null)
        {
            return pending.Task;
        }

        _micRequest = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        RequestPermissions([Manifest.Permission.RecordAudio], MicRequestCode);
        return _micRequest.Task;
    }

    public override void OnRequestPermissionsResult(
        int requestCode,
        string[] permissions,
        Permission[] grantResults)
    {
        if (requestCode == MicRequestCode)
        {
            var granted = grantResults.Length > 0 && grantResults[0] == Permission.Granted;
            var pending = _micRequest;
            _micRequest = null;
            pending?.TrySetResult(granted);
            return;
        }

        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
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

            AttachVoice();

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

    /// <summary>
    /// Wires voice once there is a session for a transcript to be sent through. Built here rather
    /// than at startup because it is useless before a connection and its warnings would have
    /// nowhere to appear.
    /// </summary>
    private void AttachVoice()
    {
        if (_session is null)
        {
            return;
        }

        _voice = AndroidVoice.TryBuild(
            _settings.Voice,
            warn: m => _viewModel.Post(() => _viewModel.System(_viewModel.Model.ActiveRoom, $"[voice] {m}")));

        if (_voice is null)
        {
            return;
        }

        _viewModel.Post(() =>
        {
            _viewModel.ReviewBeforeSend = _settings.Voice.ReviewBeforeSend;
            _viewModel.SetReadback(_voice.Policy);
        });

        _session.AttachVoice(_voice.Session, _voice.Readback);
    }

    protected override void OnDestroy()
    {
        _voice?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _session?.Dispose();
        _client?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        base.OnDestroy();
    }
}
