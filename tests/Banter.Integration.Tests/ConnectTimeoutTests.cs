using Banter.Client.Core;
using Banter.Protocol.Transport;
using Xunit;
using Xunit.Abstractions;

namespace Banter.Integration.Tests;

/// <summary>
/// A connect that is going nowhere comes back and says so.
///
/// <para>Reported from a phone: a mistyped address left the sign-in button doing nothing for
/// minutes. Nothing in Banter was waiting - the platform was. A TCP connect to an address that is
/// routable but has nothing listening waits on a SYN that is never answered, and Android's budget
/// for that is around two minutes. A mistyped address is the common case on the one device with no
/// keyboard, so it has to fail like a mistake rather than like a freeze.</para>
/// </summary>
public sealed class ConnectTimeoutTests(ITestOutputHelper output)
{
    /// <summary>
    /// 203.0.113.0/24 is TEST-NET-3 (RFC 5737): reserved for documentation, routed nowhere, and
    /// therefore the closest thing to a reliably unanswering address. A packet to it is dropped
    /// rather than refused, which is the case that hangs - a refusal would come back at once and
    /// prove nothing.
    /// </summary>
    private const string Nowhere = "tcp://203.0.113.1:7770";

    [Fact]
    public async Task AnAddressThatNeverAnswersGivesUp()
    {
        var options = new BanterClientOptions { ConnectTimeout = TimeSpan.FromSeconds(2) };
        var started = DateTimeOffset.UtcNow;

        var failed = await Assert.ThrowsAsync<BanterClientException>(() =>
            BanterClient.ConnectAsync(
                new TcpBanterTransport(), new Uri(Nowhere), "alice", "pw", options));

        var took = DateTimeOffset.UtcNow - started;
        output.WriteLine($"gave up after {took.TotalSeconds:0.0}s: {failed.Message}");

        // Generously bounded: the point is seconds rather than minutes, not the exact number.
        Assert.True(took < TimeSpan.FromSeconds(20), $"took {took.TotalSeconds:0.0}s");

        // And it says what happened, because an empty reason on a sign-in screen is the thing
        // being fixed as much as the wait is.
        Assert.Contains("did not answer", failed.Message, StringComparison.Ordinal);
        Assert.Contains("203.0.113.1", failed.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Cancelling is still cancelling. The budget is a ceiling added to the caller's token, not a
    /// replacement for it, so abandoning a sign-in stops at once and does not read as a timeout.
    /// </summary>
    [Fact]
    public async Task AbandoningItStopsAtOnce()
    {
        using var abandoned = new CancellationTokenSource();
        var options = new BanterClientOptions { ConnectTimeout = TimeSpan.FromMinutes(5) };

        var dialling = BanterClient.ConnectAsync(
            new TcpBanterTransport(), new Uri(Nowhere), "alice", "pw", options, abandoned.Token);

        await Task.Delay(100);
        abandoned.Cancel();

        var started = DateTimeOffset.UtcNow;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dialling);
        output.WriteLine($"stopped {(DateTimeOffset.UtcNow - started).TotalMilliseconds:0}ms after cancelling");
    }
}
