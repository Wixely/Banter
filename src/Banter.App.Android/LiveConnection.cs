using Android.Content;
using Banter.App;
using Banter.Client.Core;
using Banter.Protocol;

namespace Banter.App.Android;

/// <summary>
/// The connection, and the model it feeds, held where an activity ending cannot take them.
///
/// <para><b>Why static.</b> An Android activity is a view of an app, not the app: the system
/// destroys and rebuilds it for reasons that have nothing to do with whether you are still in a
/// conversation — swiping the task away, memory pressure, a configuration change this head does
/// not already absorb. A session owned by the activity dies with it, and a foreground service
/// holding a notification that says "Connected" over a socket that closed with the last activity
/// would be a lie the user cannot dismiss.</para>
///
/// <para>The view model is here for the same reason and not a separate one: the session pumps
/// into it, so a surviving session bound to a discarded model would receive messages into nothing.
/// It costs nothing to keep — it holds no window, document or engine handle by design, which is
/// what makes it testable elsewhere and portable here.</para>
///
/// <para>This is the process's state, and dies with the process. Nothing is persisted and nothing
/// is resumed: if Android kills the app outright, the next launch starts at the connect screen,
/// which is the truth.</para>
/// </summary>
internal static class LiveConnection
{
    /// <summary>Built once per process and handed to every activity that asks.</summary>
    internal static ChatViewModel ViewModel { get; } = new();

    internal static BanterClient? Client { get; private set; }

    internal static BanterChatSession? Session { get; private set; }

    /// <summary>What was signed in to, for the notification to name and a rebuilt activity to
    /// show.</summary>
    internal static string Server { get; private set; } = string.Empty;

    internal static string User { get; private set; } = string.Empty;

    /// <summary>Whether a session exists, which is the only sense in which this app is "signed
    /// in".</summary>
    internal static bool IsLive => Session is not null;

    /// <summary>
    /// Whether an activity is in front of the user right now.
    ///
    /// <para>Read before posting a mention: a notification for a message you are looking at is
    /// noise, and the phone is the device where that is most obviously true.</para>
    /// </summary>
    internal static bool InForeground { get; set; }

    /// <summary>
    /// The application context, not an activity's. Notifications are posted from here precisely
    /// when there is no activity, and a destroyed one used as a context is both a leak and a lie.
    /// </summary>
    private static Context? _app;

    /// <summary>Whether to tell the user when they are named. Seeded at sign-in and updated whenever the
    /// settings page changes it, since this has no way to read settings itself.</summary>
    internal static bool NotifyOnMention { get; set; }

    internal static void Adopt(
        BanterClient client,
        BanterChatSession session,
        string server,
        string user,
        Context app,
        bool notifyOnMention)
    {
        Client = client;
        Session = session;
        Server = server;
        User = user;
        _app = app;
        NotifyOnMention = notifyOnMention;

        // Straight off the client, deliberately. The view model is only ever touched by the frame
        // loop, and a backgrounded app draws no frames — so a mention noticed there is noticed
        // only once somebody is already looking, which is exactly too late to be told about.
        client.MessageReceived += Mentioned;
    }

    /// <summary>
    /// Somebody said something. Called on the receive thread, in whatever state the app happens to
    /// be in including none, so it touches nothing that belongs to a frame or an activity.
    /// </summary>
    private static void Mentioned(MsgPayload message)
    {
        // Not while it is on the screen: a notification for a message you are watching arrive is
        // noise, and the room itself has already said it.
        if (InForeground || !NotifyOnMention || _app is null || !NamesUs(message))
        {
            return;
        }

        Notifications.Mentioned(_app, message.Room, message.Sender, message.Text);
    }

    /// <summary>
    /// Ends it. Safe when there is nothing to end, because the two callers — signing out and an
    /// activity going away for good — cannot both know whether the other has been here first.
    /// </summary>
    internal static async Task EndAsync()
    {
        var session = Session;
        var client = Client;
        Session = null;
        Client = null;

        if (client is not null)
        {
            client.MessageReceived -= Mentioned;
        }

        session?.Dispose();

        if (client is not null)
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Whether a message names the signed-in user, by the same rule the timeline highlights one
    /// with — so a phone never buzzes for something the room would not have marked, and never
    /// stays quiet for something it would.
    /// </summary>
    internal static bool NamesUs(MsgPayload message) =>
        !string.Equals(message.Sender, ViewModel.Model.Nick, StringComparison.OrdinalIgnoreCase)
        && ViewModel.MentionsMe(message.Text);
}
