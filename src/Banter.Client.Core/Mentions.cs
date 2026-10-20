namespace Banter.Client.Core;

/// <summary>
/// What counts as naming somebody with an <c>@</c>. One rule, because agents decide whether to
/// answer by it and the client shows who will answer by it: two copies that disagree put a warning
/// in the composer for an agent that then stays quiet, or none for one that answers.
/// </summary>
public static class Mentions
{
    /// <summary>
    /// Whether <paramref name="text"/> carries <c>@nick</c>, ending where the nick does.
    ///
    /// <para>"@scouting" is not scout, and nor is "@scout-2" or "@scout_b": a nick may hold almost
    /// anything but a space, '#' or '@', and those two are how nicks are commonly numbered, so
    /// they continue it. A full stop continues it only when more of a name follows - "@scout." at
    /// the end of a sentence still names scout, "@scout.b" does not.</para>
    ///
    /// <para>Every occurrence is tried, so "@scout-2 and @scout" names scout.</para>
    /// </summary>
    public static bool Names(string text, string nick)
    {
        if (nick.Length == 0)
        {
            return false;
        }

        var at = text.IndexOf('@' + nick, StringComparison.OrdinalIgnoreCase);
        while (at >= 0)
        {
            var after = at + 1 + nick.Length;
            if (!Continues(text, after))
            {
                return true;
            }

            at = text.IndexOf('@' + nick, after, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static bool Continues(string text, int i)
    {
        if (i >= text.Length)
        {
            return false;
        }

        var c = text[i];
        if (char.IsLetterOrDigit(c) || c is '-' or '_')
        {
            return true;
        }

        return c == '.' && i + 1 < text.Length && char.IsLetterOrDigit(text[i + 1]);
    }
}
