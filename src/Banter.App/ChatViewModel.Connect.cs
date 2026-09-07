namespace Banter.App;

/// <summary>
/// The connect screen's state.
///
/// <para>Every head shows this now. It began as the Android answer to having no command line, and
/// the desktop head kept exiting to a terminal nobody was looking at — a windowed application
/// launched from a shortcut printed "no server/user configured" to a console that did not exist
/// and vanished. A sign-in screen is the answer to that on any platform, so it lives in the
/// shared app and the command line became a way to skip it rather than the only way in.</para>
/// </summary>
public sealed partial class ChatViewModel
{
    /// <summary>Whether the connect screen is in front of the timeline.</summary>
    public bool ConnectVisible => !Model.ConnectClass.Contains("hidden", StringComparison.Ordinal);

    /// <summary>
    /// Shows the connect screen, pre-filled with whatever was remembered. The password is never
    /// pre-filled: a stored one is used to connect without showing this screen at all, so
    /// arriving here means there is nothing usable to fill in — either nothing was kept, or what
    /// was kept has just been refused.
    /// </summary>
    public void ShowConnect(string server, string user, string? hint = null)
    {
        Model.ConnectServer = server;
        Model.ConnectUser = user;
        Model.ConnectPassword = "";
        Model.ConnectStatus = "";
        Model.ConnectButtonText = "Connect";
        Model.ConnectClass = "connect";
        // See ChatModel.MainClass: with the chat pane on screen Tab cannot move between these
        // fields at all, and would reach the composer if it could.
        Model.MainClass = "main hidden";

        if (hint is not null)
        {
            Model.ConnectHint = hint;
        }
    }

    /// <summary>
    /// Offers a server address the head learned after the screen was already up — a link a node
    /// published, say. Declined once someone has typed their own, and once an attempt is under way:
    /// a field that rewrites itself under the person filling it in is worse than one left alone.
    /// </summary>
    public void SuggestConnectServer(string server)
    {
        if (server.Length == 0 || Model.ConnectServer.Length != 0 || !ConnectVisible)
        {
            return;
        }

        Model.ConnectServer = server;
    }

    /// <summary>An attempt is under way. The button is disabled by saying so, not by a flag.</summary>
    public void Connecting()
    {
        Model.ConnectStatus = "Connecting...";
        Model.ConnectButtonText = "Connecting";
    }

    /// <summary>
    /// The attempt failed. The screen stays up with the reason on it, and the password is cleared
    /// — the most likely reason is that it was wrong, and a stale one in the box invites the same
    /// failure again.
    /// </summary>
    public void ConnectFailed(string reason)
    {
        Model.ConnectStatus = reason;
        Model.ConnectPassword = "";
        Model.ConnectButtonText = "Connect";
    }

    /// <summary>
    /// Connected: the screen goes away and the secret goes with it. The account is recorded for
    /// the settings page, which is the only place it is visible once the screen is down — and
    /// the only place to leave it from.
    /// </summary>
    public void Connected(string server = "", string user = "")
    {
        Model.ConnectPassword = "";
        Model.ConnectStatus = "";
        Model.ConnectClass = "connect hidden";
        Model.MainClass = "main";

        if (server.Length > 0)
        {
            Model.AccountServer = server;
            Model.AccountUser = user;
            Model.SignOutClass = "mgmt-remove sign-out";
        }
    }

    /// <summary>
    /// Back to the sign-in screen, having ended the session. Rooms and their backlogs go with it:
    /// leaving the previous account's conversation on screen behind the sign-in card, waiting for
    /// whoever signs in next, is not a thing a client should do.
    /// </summary>
    public void SignedOut(string server, string user, string reason = "")
    {
        // The private caches as well as what is on screen. Clearing only Model.Messages would
        // leave the previous account's backlog in _rooms, ready to reappear the moment the next
        // one joined a room with the same name.
        _rooms.Clear();
        _cursors.Clear();
        _streams.Clear();
        _prepended = 0;

        Model.Rooms.Clear();
        Model.Messages.Clear();
        Model.Browse.Clear();
        Model.ActiveRoom = "";
        Model.Topic = "";
        SetNick("");
        Model.SignOutClass = "mgmt-remove sign-out hidden";
        Model.SettingsPanelClass = "mgmt hidden";

        SetStatus("Not connected", connected: false);
        ShowConnect(server, user);
        Model.ConnectStatus = reason;
    }

    /// <summary>
    /// What the connect form currently holds, trimmed. Returns false when something required is
    /// missing, having said which — a form that simply does nothing when tapped reads as broken.
    /// </summary>
    public bool TryReadConnect(out string server, out string user, out string password)
    {
        server = Model.ConnectServer.Trim();
        user = Model.ConnectUser.Trim();
        password = Model.ConnectPassword;

        var missing =
            server.Length == 0 ? "a server" :
            user.Length == 0 ? "a name" :
            password.Length == 0 ? "a password" :
            !Uri.TryCreate(server, UriKind.Absolute, out _) ? null : "";

        if (missing == "")
        {
            return true;
        }

        Model.ConnectStatus = missing is null
            ? $"'{server}' is not a server address."
            : $"Needs {missing}.";
        return false;
    }
}
