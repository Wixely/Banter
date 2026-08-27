namespace Banter.App;

/// <summary>
/// The connect screen's state.
///
/// <para>A desktop head is given its server and account on the command line and never shows this.
/// A phone has no command line, so this is where an account is entered — which is why it lives in
/// the shared app rather than in the Android head.</para>
/// </summary>
public sealed partial class ChatViewModel
{
    /// <summary>Whether the connect screen is in front of the timeline.</summary>
    public bool ConnectVisible => !Model.ConnectClass.Contains("hidden", StringComparison.Ordinal);

    /// <summary>
    /// Shows the connect screen, pre-filled with whatever was remembered. The password is never
    /// among that — it is not stored, so it is asked for every time.
    /// </summary>
    public void ShowConnect(string server, string user)
    {
        Model.ConnectServer = server;
        Model.ConnectUser = user;
        Model.ConnectPassword = "";
        Model.ConnectStatus = "";
        Model.ConnectButtonText = "Connect";
        Model.ConnectClass = "connect";
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

    /// <summary>Connected: the screen goes away and the secret goes with it.</summary>
    public void Connected()
    {
        Model.ConnectPassword = "";
        Model.ConnectStatus = "";
        Model.ConnectClass = "connect hidden";
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
