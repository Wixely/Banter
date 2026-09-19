using Android;
using Android.App;
using Android.Content.PM;
using Banter.App;
using Banter.Client.Core;
using Banter.Protocol.Transport;
using Banter.Transport.Shrine;
using CupriNet.Alembic.BouncyCastle;
using CupriNet.Vessel;
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

    /// <summary>The file pick in flight, completed by <see cref="OnActivityResult"/>.</summary>
    private TaskCompletionSource<string?>? _pickRequest;

    private const int PickRequestCode = 4102;

    protected override CupriApp CreateApp()
    {
        _settings = BanterSettings.Load(problem: p => global::Android.Util.Log.Warn(LogTag, $"settings: {p}"));

        _viewModel.SetStatus("Not connected", connected: false);
        _viewModel.ShowConnect(_settings.Server, _settings.User);

        // Shows the attach control. Direct rather than posted, like the two calls above: this runs
        // on the UI thread before there is a document to refresh, so the first frame already has
        // it rather than gaining it a frame later.
        _viewModel.EnableAttach();

        return new BanterChatApp(_viewModel)
        {
            ConnectAsync = ConnectAsync,
            SignOutAsync = SignOutAsync,
            SendAsync = (room, text) => _session?.SendAsync(room, text) ?? Task.CompletedTask,
            ReplyAsync = (room, text, replyTo) => _session?.SendAsync(room, text, replyTo) ?? Task.CompletedTask,
            AnswerAsync = answer => _session?.AnswerAsync(answer) ?? Task.CompletedTask,
            CommandAsync = (room, line) => _session?.CommandAsync(room, line) ?? Task.CompletedTask,
            LoadOlderAsync = room => _session?.LoadOlderAsync(room, _settings.HistoryPageSize) ?? Task.CompletedTask,
            DownloadAsync = fileId => _session?.DownloadAsync(fileId) ?? Task.CompletedTask,
            JoinRoomAsync = room => _session?.JoinAsync(room, _settings.HistoryPageSize) ?? Task.CompletedTask,
            ToolsOpenAsync = agent => _session?.LoadToolsAsync(agent) ?? Task.CompletedTask,
            ToolsSaveAsync = (agent, tools) => _session?.SaveToolsAsync(agent, tools) ?? Task.CompletedTask,
            // The phone is the device with the camera and the photo library on it, and until now
            // it was the one head that could not send either: EnableAttach was called from the
            // desktop head alone, so the control never appeared here.
            FilePicker = new AndroidFilePicker(this),
            // Quoted, because the picker's copy is named after what the user knows the file as
            // and that name can contain spaces.
            AttachAsync = (room, path) => _session?.UploadAsync(room, $"\"{path}\"") ?? Task.CompletedTask,
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
    /// Opens the system picker and waits for it. One at a time: a second request while one is in
    /// flight cancels the first rather than leaving a task nothing will ever complete — the
    /// picker is a separate activity, and the only way to ask twice is to have got back here,
    /// which means the first answer is never coming.
    /// </summary>
    internal Task<string?> PickFileAsync(global::Android.Content.Intent chooser, CancellationToken cancellationToken)
    {
        _pickRequest?.TrySetResult(null);

        var request = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pickRequest = request;
        cancellationToken.Register(() => request.TrySetResult(null));

        try
        {
            StartActivityForResult(chooser, PickRequestCode);
        }
        catch (global::Android.Content.ActivityNotFoundException)
        {
            // No document provider on the device at all. Rare, but a bare phone image can be like
            // this, and it is a cancel rather than a crash.
            _pickRequest = null;
            return Task.FromResult<string?>(null);
        }

        return request.Task;
    }

    protected override void OnActivityResult(
        int requestCode,
        global::Android.App.Result resultCode,
        global::Android.Content.Intent? data)
    {
        if (requestCode == PickRequestCode)
        {
            var pending = _pickRequest;
            _pickRequest = null;

            // Cancelled is the ordinary outcome and says nothing; only a chosen item has a URI.
            if (resultCode != global::Android.App.Result.Ok || data?.Data is not { } uri)
            {
                pending?.TrySetResult(null);
                return;
            }

            string? path = null;
            try
            {
                path = AndroidFilePicker.Materialise(this, uri);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                           or global::Java.Lang.SecurityException)
            {
                // Copying is the step that can fail for reasons the user can do nothing about:
                // a revoked grant, a provider that died, a full cache. Say so in the room rather
                // than taking the app down over an attachment.
                global::Android.Util.Log.Warn(LogTag, $"attach: {ex.Message}");
                _viewModel.Post(() => _viewModel.System(
                    _viewModel.Model.ActiveRoom, "could not read that file."));
            }

            pending?.TrySetResult(path);
            return;
        }

        base.OnActivityResult(requestCode, resultCode, data);
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

            // Built-in schemes first; anything else is a CupriNet link, which BanterTransports
            // cannot resolve because the mesh lives outside Banter.Protocol.
            var transport = BanterTransports.TryClient(uri) ?? BuildMesh();

            _client = await BanterClient
                .ConnectAsync(transport, uri, user, password)
                .ConfigureAwait(false);

            _session = new BanterChatSession(_client, _viewModel);

            _viewModel.Post(() =>
            {
                _viewModel.SetNick(_client.Nick);
                _viewModel.SetStatus("Connected", connected: true);
                _viewModel.Connected(server, user);
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
    /// A transport for a CupriNet link: a TCP vessel to the node, a Pilgrimage over it to the site
    /// <c>banter-nodestar</c> serves, and Banter frames on a conduit inside that.
    ///
    /// <para>The same site the web head reaches, and the same code above the vessel — the browser
    /// differs only in carrying its vessel on a WebRTC DataChannel. A phone cannot do that: the
    /// managed WebRTC in the estate is the <em>answering</em> half, an ICE-lite responder in the
    /// DTLS server role built so a node can accept a browser. There is no client to dial with, so
    /// the phone dials TCP.</para>
    ///
    /// <para>So this is not NAT traversal, and it is worth being plain about that: the node still
    /// has to be reachable. What it is instead of <c>tcp://</c> is an authenticated one — the
    /// Pilgrimage pins the site's own Signet and the frames ride an encrypted conduit, where a
    /// plain socket pins nothing and encrypts nothing.</para>
    /// </summary>
    private IBanterClientTransport BuildMesh() =>
        new ShrineClientTransport(
            async (intonation, cancellationToken) =>
            {
                // The link names the node's reachable addresses but not which port serves which
                // rite, so the host comes from the beacons and the port from settings.
                var host = MeshDial.HostOrThrow(intonation.Beacons, intonation.Moniker ?? "the server");

                return await TcpVessel
                    .ConnectAsync(host, _settings.MeshVesselPort, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            },
            new BouncyCastleSuite());

    /// <summary>
    /// Ends the session and returns to the connect screen, filled in with what was just used.
    ///
    /// <para>Nothing to forget here: this head has never stored a password, so signing out is the
    /// disconnect and nothing else. The control is shared with the desktop head, and one that did
    /// nothing on a phone would be worse than one that is absent.</para>
    /// </summary>
    private async Task SignOutAsync()
    {
        var session = _session;
        var client = _client;
        _session = null;
        _client = null;

        await (_voice?.DisposeAsync() ?? ValueTask.CompletedTask).ConfigureAwait(false);
        _voice = null;

        session?.Dispose();
        if (client is not null)
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }

        _viewModel.Post(() => _viewModel.SignedOut(_settings.Server, _settings.User));
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
            _viewModel.SetAutoSubmit(
                _settings.Voice.AutoSubmit && !_settings.Voice.ReviewBeforeSend,
                _settings.Voice.AutoSubmitDelaySeconds);
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
