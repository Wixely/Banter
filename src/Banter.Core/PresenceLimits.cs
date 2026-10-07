namespace Banter.Core;

/// <summary>
/// How long an account stays present in a room after its last session drops.
///
/// <para>Presence is per account and delivery is per session: a second device must not read as a
/// second person arriving, so a PART is announced only when the LAST session of an account goes.
/// On a desktop that is honest - a closed laptop is somebody leaving. On a phone it is not. Android
/// destroys the socket on a trip out of the foreground, so a pocket produced a PART and then a JOIN
/// in every room the account was in, for somebody who never went anywhere, and loudest of all for
/// an admin who is in all of them.</para>
///
/// <para>The per-account rule was the right idea at the wrong scale: it covers two devices at once
/// and not one device a moment apart. This is the time axis of the same rule - hold the account
/// present for a few seconds after its last session drops, and if a session returns inside that
/// window, announce nothing at all.</para>
/// </summary>
public sealed record PresenceLimits
{
    public static PresenceLimits Default { get; } = new();

    /// <summary>
    /// How long an account is held present after its last session drops. Zero announces
    /// immediately, which is what the server did before this existed.
    ///
    /// <para>Named and defaulted to match <c>BanterClientOptions.ReconnectGrace</c> on purpose:
    /// that is how long a client holds a send open expecting to be back, so a server treating the
    /// same window as "not gone yet" makes the two sides agree about what a blip is. Tuning one
    /// without the other gives a client that is still patiently waiting for a room it has already
    /// been announced as leaving.</para>
    /// </summary>
    public TimeSpan ReconnectGrace { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How often the server looks for a grace that has run out. A departure is therefore announced
    /// up to this late, which costs nothing: by then it is a line saying somebody left a room, and
    /// two seconds of it is not worth a tighter timer.
    /// </summary>
    public TimeSpan SweepInterval { get; init; } = TimeSpan.FromSeconds(2);
}
