using Android.App;
using Android.Content;
using Android.OS;

namespace Banter.App.Android;

/// <summary>
/// The two things this app has to say when nobody is looking at it: that it is holding the
/// connection open, and that somebody named you.
///
/// <para>Platform notifications rather than anything of ours, because the whole point is to be
/// seen while the app is not being drawn. A phone that has stopped presenting frames cannot tell
/// you anything through its own interface.</para>
///
/// <para>No icon resources: this head has no <c>Resources</c> directory and gains nothing from
/// one, so both notifications use the system's own chat glyph.</para>
/// </summary>
internal static class Notifications
{
    /// <summary>The ongoing "still connected" notification, which is also the foreground service's
    /// licence to exist.</summary>
    internal const string ConnectionChannel = "banter.connection";

    /// <summary>Somebody named you. Separate channel so it can be silenced on its own — and so
    /// that silencing the quiet one does not silence this.</summary>
    internal const string MentionChannel = "banter.mentions";

    internal const int ConnectionId = 1;

    private static int _nextMentionId = 100;

    /// <summary>
    /// Both channels, created once. Idempotent by design on Android: creating a channel that
    /// exists updates its name and leaves every choice the user has made about it alone.
    /// </summary>
    internal static void EnsureChannels(Context context)
    {
        if (context.GetSystemService(Context.NotificationService) is not NotificationManager manager)
        {
            return;
        }

        // Low: it is a status line, not an event. It makes no sound and does not intrude, which is
        // the most that should be asked of a notification the user is not allowed to dismiss.
        var connection = new NotificationChannel(
            ConnectionChannel,
            "Connection",
            NotificationImportance.Low);
        connection.Description = "Shown while Banter is holding the connection open in the background.";
        manager.CreateNotificationChannel(connection);

        // Default rather than High: being named is worth a sound, and is not worth taking over the
        // screen. High is for things that cannot wait, and a message can.
        var mentions = new NotificationChannel(
            MentionChannel,
            "Mentions",
            NotificationImportance.Default);
        mentions.Description = "Shown when somebody names you while you are not looking.";
        manager.CreateNotificationChannel(mentions);
    }

    /// <summary>
    /// The notification the foreground service runs under. Android requires one, and requires it
    /// to be honest: it says which server, because somebody wondering why their battery is being
    /// used deserves to know what for.
    /// </summary>
    internal static Notification Ongoing(Context context, string server) =>
        new Notification.Builder(context, ConnectionChannel)
            .SetContentTitle("Connected")
            .SetContentText(server)
            .SetSmallIcon(global::Android.Resource.Drawable.StatNotifyChat)
            .SetOngoing(true)
            // No timestamp: a notification that has been up for six hours saying "09:14" reads as
            // something that happened rather than something that is true.
            .SetShowWhen(false)
            .SetContentIntent(OpenApp(context))
            .Build();

    /// <summary>
    /// Somebody named you. One notification per message rather than a running tally: a phone
    /// already groups them, and collapsing six mentions into "6 mentions" loses the part worth
    /// reading.
    /// </summary>
    internal static void Mentioned(Context context, string room, string sender, string text)
    {
        if (!CanPost(context)
            || context.GetSystemService(Context.NotificationService) is not NotificationManager manager)
        {
            return;
        }

        var notification = new Notification.Builder(context, MentionChannel)
            .SetContentTitle($"{sender} in {room}")
            .SetContentText(text)
            // The text of a message is routinely longer than one line, and the useful half is
            // rarely the first few words.
            .SetStyle(new Notification.BigTextStyle().BigText(text))
            .SetSmallIcon(global::Android.Resource.Drawable.StatNotifyChat)
            .SetAutoCancel(true)
            .SetContentIntent(OpenApp(context))
            .Build();

        manager.Notify(Interlocked.Increment(ref _nextMentionId), notification);
    }

    /// <summary>
    /// Whether notifications may be posted at all. From Android 13 this is a runtime grant like
    /// any other, and a refusal is not an error — it is an answer, and posting into it silently
    /// does nothing.
    /// </summary>
    internal static bool CanPost(Context context)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(33))
        {
            return true;
        }

        return context.CheckSelfPermission(global::Android.Manifest.Permission.PostNotifications)
            == global::Android.Content.PM.Permission.Granted;
    }

    /// <summary>
    /// Tapping any of these opens the app rather than starting a second copy of it.
    /// <c>Immutable</c> because nothing that receives this is allowed to fill anything in, and
    /// from Android 12 saying so is required rather than advisable.
    /// </summary>
    private static PendingIntent? OpenApp(Context context)
    {
        var intent = new Intent(context, typeof(MainActivity));
        intent.SetFlags(ActivityFlags.SingleTop | ActivityFlags.ClearTop);

        return PendingIntent.GetActivity(context, 0, intent, PendingIntentFlags.Immutable);
    }
}
