using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;

namespace Banter.App.Android;

/// <summary>
/// Keeps the process in the foreground so the connection survives the phone being put down.
///
/// <para><b>What it is actually for.</b> Android destroys a backgrounded app's TCP sockets once it
/// has been away long enough — 30 to 45 seconds, measured (§7a) — and nothing warns the app: the
/// receive loop simply ends. A foreground service is what tells the system this uid is not idle.
/// It holds no connection of its own and talks to nothing; its entire job is to be running.</para>
///
/// <para><b>Why it does not own the client.</b> It could, and then it would have to own the view
/// model too, since the session pumps into one. Instead both live in
/// <see cref="LiveConnection"/>, which is static and therefore outlives the activity — so the
/// connection survives the activity being destroyed, and a new activity finds it rather than
/// building a second. The service's presence is what keeps that process alive to be found.</para>
///
/// <para><b>It is not permanent.</b> <c>dataSync</c> is capped at roughly six hours a day from
/// Android 14, after which the system stops the service and the phone goes back to losing sockets
/// when it is pocketed. That is survivable rather than fatal: a send issued into the gap waits for
/// the redial instead of vanishing, which is what the reconnect grace in
/// <c>Banter.Client.Core</c> is for.</para>
/// </summary>
[Service(
    Exported = false,
    // Declared here as well as in the manifest, because from Android 14 the system checks that the
    // type it was started with is one the service actually claims, and refuses otherwise.
    ForegroundServiceType = ForegroundService.TypeDataSync)]
public sealed class ConnectionService : Service
{
    private const string LogTag = "Banter";

    /// <summary>Which server to name in the notification. An extra rather than a field, because
    /// the system may recreate the service without going through whoever started it.</summary>
    internal const string ServerExtra = "banter.service.server";

    /// <summary>
    /// Starts holding the connection open. Safe to call when it is already running: Android
    /// delivers a second <c>OnStartCommand</c> and the notification is replaced rather than
    /// duplicated.
    /// </summary>
    internal static void Start(Context context, string server)
    {
        var intent = new Intent(context, typeof(ConnectionService));
        intent.PutExtra(ServerExtra, server);

        // StartForegroundService, not StartService: the latter is forbidden from the background
        // and this can be called from one — signing in over a link, for instance, finishes
        // whenever the network finishes.
        context.StartForegroundService(intent);
    }

    internal static void Stop(Context context) =>
        context.StopService(new Intent(context, typeof(ConnectionService)));

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        Notifications.EnsureChannels(this);

        var server = intent?.GetStringExtra(ServerExtra) ?? "a Banter server";

        try
        {
            var notification = Notifications.Ongoing(this, server);

            if (OperatingSystem.IsAndroidVersionAtLeast(29))
            {
                StartForeground(Notifications.ConnectionId, notification, ForegroundService.TypeDataSync);
            }
            else
            {
                StartForeground(Notifications.ConnectionId, notification);
            }
        }
        catch (Java.Lang.Exception ex)
        {
            // Android 12 forbids starting a foreground service from the background in most cases,
            // and 14 ends a dataSync one that has had its day's allowance. Both arrive here as a
            // throw, and neither is worth taking the app down for: the connection carries on
            // exactly as it did before this service existed, which is to say until the phone is
            // put down.
            global::Android.Util.Log.Warn(LogTag, $"stay-connected: {ex.Message}");
            StopSelf();
            return StartCommandResult.NotSticky;
        }

        // NotSticky: if the system kills this process, the connection died with it, and being
        // restarted with no session to guard would put up a notification that lies.
        return StartCommandResult.NotSticky;
    }
}
