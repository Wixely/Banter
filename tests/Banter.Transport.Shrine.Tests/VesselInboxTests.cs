using Banter.Transport.Shrine;
using Xunit;

namespace Banter.Transport.Shrine.Tests;

/// <summary>
/// The web head's receive path, which had no test because the class holding it cannot be built off
/// a browser.
///
/// <para>The case worth the extraction is the last few: a peer that sends something impossible must
/// not be indistinguishable from a peer that said goodbye. The browser channel's oversized-message
/// guard raised exactly that fault and then swallowed it, so the desync it exists to prevent
/// happened anyway, quietly.</para>
/// </summary>
public sealed class VesselInboxTests
{
    [Fact]
    public async Task AMessageComesBack()
    {
        var inbox = new VesselInbox();
        inbox.Deliver([1, 2, 3]);

        Assert.Equal([1, 2, 3], await inbox.ReceiveAsync());
    }

    [Fact]
    public async Task MessagesKeepTheirOrder()
    {
        var inbox = new VesselInbox();
        inbox.Deliver([1]);
        inbox.Deliver([2]);
        inbox.Deliver([3]);

        Assert.Equal([1], await inbox.ReceiveAsync());
        Assert.Equal([2], await inbox.ReceiveAsync());
        Assert.Equal([3], await inbox.ReceiveAsync());
    }

    /// <summary>An empty message is one a DataChannel can legitimately carry, so it must not read
    /// back as the end.</summary>
    [Fact]
    public async Task AnEmptyMessageIsAMessageAndNotAnEnding()
    {
        var inbox = new VesselInbox();
        inbox.Deliver([]);

        var message = await inbox.ReceiveAsync();

        Assert.NotNull(message);
        Assert.Empty(message);
    }

    [Fact]
    public async Task ThePeerLeavingReadsBackAsNull()
    {
        var inbox = new VesselInbox();
        inbox.Close();

        Assert.Null(await inbox.ReceiveAsync());
    }

    /// <summary>A receive loop must not hang on a vessel that has already ended.</summary>
    [Fact]
    public async Task ACleanCloseLatches()
    {
        var inbox = new VesselInbox();
        inbox.Close();

        Assert.Null(await inbox.ReceiveAsync());
        Assert.Null(await inbox.ReceiveAsync());
        Assert.Null(await inbox.ReceiveAsync());
    }

    [Fact]
    public async Task MessagesQueuedBeforeACloseAreStillDelivered()
    {
        var inbox = new VesselInbox();
        inbox.Deliver([7]);
        inbox.Close();

        Assert.Equal([7], await inbox.ReceiveAsync());
        Assert.Null(await inbox.ReceiveAsync());
    }

    [Fact]
    public async Task AReadAlreadyWaitingIsCompletedByAMessage()
    {
        var inbox = new VesselInbox();
        var pending = inbox.ReceiveAsync();

        inbox.Deliver([9]);

        Assert.Equal([9], await pending);
    }

    [Fact]
    public async Task AReadAlreadyWaitingIsEndedByAClose()
    {
        var inbox = new VesselInbox();
        var pending = inbox.ReceiveAsync();

        inbox.Close();

        Assert.Null(await pending);
    }

    /// <summary>The whole point: a fault does not read back as a goodbye.</summary>
    [Fact]
    public async Task AFaultIsRaisedRatherThanReadBackAsAnEnding()
    {
        var inbox = new VesselInbox();
        inbox.Fail("a WebRTC message exceeded the 262144-byte receive buffer.");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await inbox.ReceiveAsync());

        Assert.Contains("262144", ex.Message);
    }

    /// <summary>They arrived before the fault, so they are as good as any other message.</summary>
    [Fact]
    public async Task MessagesQueuedBeforeAFaultAreDeliveredFirst()
    {
        var inbox = new VesselInbox();
        inbox.Deliver([4]);
        inbox.Fail("the peer sent something impossible.");

        Assert.Equal([4], await inbox.ReceiveAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await inbox.ReceiveAsync());
    }

    /// <summary>
    /// A loop that swallowed the first fault and came back for more would otherwise be told the
    /// ending was clean — which is the bug this type exists to make impossible.
    /// </summary>
    [Fact]
    public async Task AFaultLatchesRatherThanDecayingIntoACleanClose()
    {
        var inbox = new VesselInbox();
        inbox.Fail("the peer sent something impossible.");

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await inbox.ReceiveAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await inbox.ReceiveAsync());
    }

    /// <summary>A failure usually causes a second one on the way down; the first explains it.</summary>
    [Fact]
    public async Task TheFirstReasonWins()
    {
        var inbox = new VesselInbox();
        inbox.Fail("the message was too big.");
        inbox.Fail("the channel then went away.");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await inbox.ReceiveAsync());

        Assert.Equal("the message was too big.", ex.Message);
        Assert.Equal("the message was too big.", inbox.Fault);
    }

    /// <summary>Nothing about a clean ending is a fault, and callers check this to tell them apart.</summary>
    [Fact]
    public void ACleanCloseLeavesNoFault()
    {
        var inbox = new VesselInbox();
        inbox.Close();

        Assert.Null(inbox.Fault);
    }

    /// <summary>
    /// Messages keep arriving from a callback for a moment after the vessel ends, and a callback
    /// has nowhere to put an exception.
    /// </summary>
    [Fact]
    public async Task AMessageArrivingAfterTheEndIsDroppedRatherThanThrown()
    {
        var inbox = new VesselInbox();
        inbox.Close();

        inbox.Deliver([1]);

        Assert.Null(await inbox.ReceiveAsync());
    }

    [Fact]
    public async Task AWaitingReadIsCancellable()
    {
        var inbox = new VesselInbox();
        using var cts = new CancellationTokenSource();
        var pending = inbox.ReceiveAsync(cts.Token);

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
    }
}
