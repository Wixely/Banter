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

    /// <summary>
    /// The process's view model, not this activity's. An activity is a view of the app and is
    /// destroyed for reasons that have nothing to do with the conversation, so the model it shows
    /// outlives it — see <see cref="LiveConnection"/>.
    /// </summary>
    private readonly ChatViewModel _viewModel = LiveConnection.ViewModel;

    /// <summary>The live session, wherever it was made. A property rather than a field because
    /// this activity may not be the one that opened it.</summary>
    private static BanterChatSession? Session => LiveConnection.Session;

    private BanterSettings _settings = new();

    /// <summary>
    /// Voice stays with the activity rather than moving to <see cref="LiveConnection"/>: a
    /// microphone held by a backgrounded app is a different promise from a socket held by one, and
    /// not one this makes. It is rebuilt when an activity is.
    /// </summary>
    private AndroidVoice? _voice;

    /// <summary>The microphone request in flight, completed by <see cref="OnRequestPermissionsResult"/>.</summary>
    private TaskCompletionSource<bool>? _micRequest;

    private const int MicRequestCode = 4101;

    /// <summary>The file pick in flight, completed by <see cref="OnActivityResult"/>.</summary>
    private TaskCompletionSource<string?>? _pickRequest;

    private const int PickRequestCode = 4102;

    /// <summary>The scan in flight, completed by <see cref="OnActivityResult"/>.</summary>
    private TaskCompletionSource<string?>? _scanRequest;

    private const int ScanRequestCode = 4103;

    /// <summary>The camera request in flight, completed by <see cref="OnRequestPermissionsResult"/>.</summary>
    private TaskCompletionSource<bool>? _cameraRequest;

    private const int CameraRequestCode = 4104;

    /// <summary>The notification request in flight, completed by <see cref="OnRequestPermissionsResult"/>.</summary>
    private TaskCompletionSource<bool>? _notifyRequest;

    private const int NotifyRequestCode = 4105;

    protected override CupriApp CreateApp()
    {
        _settings = BanterSettings.Load(problem: p => global::Android.Util.Log.Warn(LogTag, $"settings: {p}"));

        if (LiveConnection.IsLive)
        {
            // This activity is a replacement, not a first one: the session outlived whichever
            // activity opened it. Show the conversation it is already in rather than a connect
            // form for a connection that exists.
            _viewModel.SetStatus("Connected", connected: true);
            _viewModel.Connected(LiveConnection.Server, LiveConnection.User);
        }
        else
        {
            _viewModel.SetStatus("Not connected", connected: false);
            _viewModel.ShowConnect(_settings.Server, _settings.User);
        }

        // Shows the attach control. Direct rather than posted, like the two calls above: this runs
        // on the UI thread before there is a document to refresh, so the first frame already has
        // it rather than gaining it a frame later.
        _viewModel.EnableAttach();

        // The phone is the head that needs this and the only one that can do it: a signed link is
        // ~380 characters, and this is the device with no keyboard worth typing that on and a
        // camera to avoid it.
        _viewModel.EnableScan();

        // The one setting that only exists here, because it is the only head whose platform takes
        // the connection away.
        _viewModel.EnableStayConnected(_settings.StayConnected);

        // This head has a notification shade rather than a taskbar, and — until now — no rendered
        // control at all: the alerts page showed a heading and a hint above nothing, because
        // nobody had ever seeded the choices here the way the desktop head does.
        _viewModel.EnableNotificationAlerts();
        _viewModel.SetFlashOnMention(_settings.FlashOnMention);

        return new BanterChatApp(_viewModel)
        {
            ConnectAsync = ConnectAsync,
            SignOutAsync = SignOutAsync,
            SendAsync = (room, text) => Session?.SendAsync(room, text) ?? Task.CompletedTask,
            ReplyAsync = (room, text, replyTo) => Session?.SendAsync(room, text, replyTo) ?? Task.CompletedTask,
            AnswerAsync = answer => Session?.AnswerAsync(answer) ?? Task.CompletedTask,
            CommandAsync = (room, line) => Session?.CommandAsync(room, line) ?? Task.CompletedTask,
            LoadOlderAsync = room => Session?.LoadOlderAsync(room, _settings.HistoryPageSize) ?? Task.CompletedTask,
            DownloadAsync = fileId => Session?.DownloadAsync(fileId) ?? Task.CompletedTask,
            JoinRoomAsync = room => Session?.JoinAsync(room, _settings.HistoryPageSize) ?? Task.CompletedTask,
            ToolsOpenAsync = agent => Session?.LoadToolsAsync(agent) ?? Task.CompletedTask,
            ToolsSaveAsync = (agent, tools) => Session?.SaveToolsAsync(agent, tools) ?? Task.CompletedTask,
            // The phone is the device with the camera and the photo library on it, and until now
            // it was the one head that could not send either: EnableAttach was called from the
            // desktop head alone, so the control never appeared here.
            FilePicker = new AndroidFilePicker(this),
            ScanServerAsync = ScanServerAsync,
            StayConnectedChanged = StayConnectedChanged,
            VoiceSettingsChanged = VoiceSettingsChanged,
            // Quoted, because the picker's copy is named after what the user knows the file as
            // and that name can contain spaces.
            AttachAsync = (room, path) => Session?.UploadAsync(room, $"\"{path}\"") ?? Task.CompletedTask,
            VoiceToggleAsync = SetVoiceOpenAsync,
            ReadbackChangedAsync = policy => Session?.SetReadbackAsync(policy) ?? Task.CompletedTask,

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
            await (Session?.SetVoiceOpenAsync(false) ?? Task.CompletedTask).ConfigureAwait(false);
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

        await (Session?.SetVoiceOpenAsync(true) ?? Task.CompletedTask).ConfigureAwait(false);
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

        if (requestCode == CameraRequestCode)
        {
            var granted = grantResults.Length > 0 && grantResults[0] == Permission.Granted;
            var pending = _cameraRequest;
            _cameraRequest = null;
            pending?.TrySetResult(granted);
            return;
        }

        if (requestCode == NotifyRequestCode)
        {
            var granted = grantResults.Length > 0 && grantResults[0] == Permission.Granted;
            var pending = _notifyRequest;
            _notifyRequest = null;
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

        if (requestCode == ScanRequestCode)
        {
            var pending = _scanRequest;
            _scanRequest = null;
            pending?.TrySetResult(
                resultCode == global::Android.App.Result.Ok
                    ? data?.GetStringExtra(ScannerActivity.ServerExtra)
                    : null);
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

            var client = await BanterClient
                .ConnectAsync(transport, uri, user, password)
                .ConfigureAwait(false);

            var session = new BanterChatSession(client, _viewModel);

            // ApplicationContext, not this: what it is adopted into outlives every activity, and a
            // notification posted from a destroyed one is a leak wearing a lie.
            LiveConnection.Adopt(
                client,
                session,
                server,
                user,
                ApplicationContext!,
                _settings.FlashOnMention);

            _viewModel.Post(() =>
            {
                _viewModel.SetNick(client.Nick);
                _viewModel.SetStatus("Connected", connected: true);
                _viewModel.Connected(server, user);
            });

            await StayConnectedAsync(server).ConfigureAwait(false);

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
                    await session.JoinAsync(room, _settings.HistoryPageSize).ConfigureAwait(false);
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
    /// Starts holding the connection open, if that is what the user asked for.
    ///
    /// <para>Off by default, so this usually does nothing at all. Asking for notifications here
    /// rather than at launch follows the same rule as the camera and the microphone: at the moment
    /// the thing that needs it is actually wanted, where a refusal is an answer about a feature
    /// rather than a reflex about an app that has not done anything yet.</para>
    /// </summary>
    private async Task StayConnectedAsync(string server)
    {
        if (!_settings.StayConnected)
        {
            return;
        }

        // A refusal is not a reason to skip the service. The connection is the point; the
        // notification is how Android makes us declare it, and a system that will not show it
        // still lets the service run.
        await EnsureNotificationsAsync().ConfigureAwait(false);

        ConnectionService.Start(this, server);
    }

    /// <summary>
    /// Anything on the settings page that is not the background connection. Wired here for the
    /// first time: this head could change these and never keep them, because a phone's settings
    /// file lives in app-private storage where nobody can edit it by hand — so an unsaved setting
    /// on a phone is not an inconvenience, it is a setting that does not exist.
    /// </summary>
    private void VoiceSettingsChanged()
    {
        _settings = _settings with
        {
            FlashOnMention = _viewModel.FlashOnMention,
            Voice = _settings.Voice with
            {
                Engine = _viewModel.ChosenTranscribe,
                Language = _viewModel.Model.VoiceLanguage.Trim(),
                Vocabulary = _viewModel.Model.VoiceVocabulary.Trim(),
                Endpoint = _viewModel.Model.VoiceEndpoint.Trim(),
                WyomingTts = _viewModel.Model.VoiceWyomingTts.Trim(),
                AutoSubmit = _viewModel.ChosenAutoSubmit,
                AutoSubmitDelaySeconds = _viewModel.ReadAutoSubmitDelay(),
            },
        };

        _settings.TrySave(problem: p => global::Android.Util.Log.Warn(LogTag, $"settings: {p}"));

        // The notifier reads this at sign-in and would otherwise keep whatever it was told then.
        LiveConnection.NotifyOnMention = _viewModel.FlashOnMention;
    }

    /// <summary>
    /// The setting changed. Remembered immediately, and acted on immediately: a setting that only
    /// takes effect next time you sign in is one people conclude is broken.
    /// </summary>
    private void StayConnectedChanged(bool stay)
    {
        _settings = _settings with { StayConnected = stay };
        _settings.TrySave(problem: p => global::Android.Util.Log.Warn(LogTag, $"settings: {p}"));

        if (!stay)
        {
            ConnectionService.Stop(this);
            return;
        }

        if (LiveConnection.IsLive)
        {
            // Fire and forget: the permission dialog is the user's to answer in their own time,
            // and the service does not wait on the answer either way.
            _ = StayConnectedAsync(LiveConnection.Server);
        }
    }

    private Task<bool> EnsureNotificationsAsync()
    {
        // Below 33 the permission does not exist to be asked for — installing the app was the
        // consent. The version test is repeated from CanPost rather than trusted through it
        // because the analyser cannot see through a call, and it is right not to.
        if (!OperatingSystem.IsAndroidVersionAtLeast(33) || Notifications.CanPost(this))
        {
            return Task.FromResult(true);
        }

        var pending = _notifyRequest;
        if (pending is not null)
        {
            return pending.Task;
        }

        _notifyRequest = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        RequestPermissions([Manifest.Permission.PostNotifications], NotifyRequestCode);
        return _notifyRequest.Task;
    }

    /// <summary>
    /// Opens the scanner and waits for it, asking for the camera the first time it is wanted.
    ///
    /// <para>One at a time, and for the same reason the file pick is: the scanner is a separate
    /// activity, so the only way to be asked twice is to have come back from the first — which
    /// means the first answer is never arriving.</para>
    /// </summary>
    private async Task<string?> ScanServerAsync()
    {
        if (!await EnsureCameraAsync().ConfigureAwait(false))
        {
            _viewModel.Post(() => _viewModel.ConnectFailed("camera permission was refused."));
            return null;
        }

        _scanRequest?.TrySetResult(null);

        var request = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _scanRequest = request;

        try
        {
            StartActivityForResult(new global::Android.Content.Intent(this, typeof(ScannerActivity)), ScanRequestCode);
        }
        catch (global::Android.Content.ActivityNotFoundException)
        {
            _scanRequest = null;
            return null;
        }

        return await request.Task.ConfigureAwait(false);
    }

    private Task<bool> EnsureCameraAsync()
    {
        if (CheckSelfPermission(Manifest.Permission.Camera) == Permission.Granted)
        {
            return Task.FromResult(true);
        }

        var pending = _cameraRequest;
        if (pending is not null)
        {
            return pending.Task;
        }

        _cameraRequest = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        RequestPermissions([Manifest.Permission.Camera], CameraRequestCode);
        return _cameraRequest.Task;
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
        // First, so the notification goes at the moment the connection does rather than a beat
        // after it: an app that says it is connected while signing out is worse than one that says
        // nothing.
        ConnectionService.Stop(this);

        await (_voice?.DisposeAsync() ?? ValueTask.CompletedTask).ConfigureAwait(false);
        _voice = null;

        await LiveConnection.EndAsync().ConfigureAwait(false);

        _viewModel.Post(() => _viewModel.SignedOut(_settings.Server, _settings.User));
    }

    /// <summary>
    /// Wires voice once there is a session for a transcript to be sent through. Built here rather
    /// than at startup because it is useless before a connection and its warnings would have
    /// nowhere to appear.
    /// </summary>
    private void AttachVoice()
    {
        if (Session is null)
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

        Session.AttachVoice(_voice.Session, _voice.Readback);
    }

    /// <summary>
    /// In front of the user, so a mention needs no notification — they are looking at it.
    /// </summary>
    protected override void OnResume()
    {
        base.OnResume();
        LiveConnection.InForeground = true;
    }

    protected override void OnPause()
    {
        LiveConnection.InForeground = false;
        base.OnPause();
    }

    /// <summary>
    /// This activity is going. Whether the <em>connection</em> goes with it is the whole question
    /// this change exists to answer.
    ///
    /// <para>With "stay connected" on, it does not: the session lives in
    /// <see cref="LiveConnection"/> and the foreground service keeps the process around to hold
    /// it, so a swiped-away task or an activity the system reclaimed comes back to the room it
    /// left. With it off, this is the end of the app in every sense the user means, and holding a
    /// socket open for an app somebody closed would be indefensible.</para>
    ///
    /// <para>Voice goes either way. It belongs to the activity that has the microphone permission
    /// and the screen to show what it heard.</para>
    /// </summary>
    protected override void OnDestroy()
    {
        LiveConnection.InForeground = false;

        _voice?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _voice = null;

        if (!_settings.StayConnected)
        {
            LiveConnection.EndAsync().GetAwaiter().GetResult();
        }

        base.OnDestroy();
    }
}
