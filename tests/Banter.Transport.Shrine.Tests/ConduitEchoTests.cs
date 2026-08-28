using System.Net;
using Banter.Protocol.Transport;
using CupriNet.Alembic.BouncyCastle;
using CupriNet.Core;
using CupriNet.Hosting;
using CupriNet.Nodestar;
using CupriNet.Rites;
using CupriNet.Vessel;
using Xunit;
using Xunit.Abstractions;

namespace Banter.Transport.Shrine.Tests;

/// <summary>
/// Does a conduit carry bytes at all, between a real node and a real Pilgrim? Deliberately below
/// Banter: no server, no handshake, one frame echoed. When the end-to-end test fails this says
/// whether the fault is in the conduit or in what Banter does over it.
///
/// <para>This test was the reproduction for CupriNodestar#2, where the answer was neither: nothing
/// had reached the site at all. A TCP connection to the node's own listen port reaches the
/// <i>node</i>, which completes a node-to-node handshake and has no Shrine behind it. Serving a
/// site over a vessel is a separate act — <c>AcceptPilgrimageAsync</c> — and the Pilgrim pins the
/// site's Signet, not the node's.</para>
/// </summary>
public sealed class ConduitEchoTests(ITestOutputHelper output)
{
    private static int FreePort()
    {
        using var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    [Fact]
    public async Task AFrameSentByAPilgrimComesBackFromTheSite()
    {
        var root = Path.Combine(Path.GetTempPath(), "banter-echo-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(root);

        var builder = NodestarApplication.CreateBuilder([]);
        builder.Node.Concordium = "banter-echo-test";
        builder.Node.DataDirectory = Path.Combine(root, "mesh");
        builder.Node.ListenAddress = "127.0.0.1";
        builder.Node.ListenPort = FreePort();
        builder.Node.SiteName = "Echo";
        builder.Node.Moniker = "echo-node";
        builder.Node.AdvertiseSiteInLink = true;
        builder.Node.EnableWebRtc = false;
        builder.Node.EnableTor = false;
        builder.Node.EnableWebFront = false;
        builder.Node.EnableLanDiscovery = false;
        builder.Node.EnablePortMapping = false;

        var arrived = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        builder.Site.OnSession(BanterConduit.ProtocolId, async (session, ct) =>
        {
            output.WriteLine($"site: session opened, MaxFrameBytes={session.MaxFrameBytes}");
            while (await session.ReceiveAsync(ct) is { } frame)
            {
                var text = System.Text.Encoding.UTF8.GetString(frame);
                output.WriteLine($"site: received '{text}'");
                arrived.TrySetResult(text);
                await session.SendAsync(System.Text.Encoding.UTF8.GetBytes(text.ToUpperInvariant()), ct);
            }

            output.WriteLine($"site: session ended ({session.EndReason ?? "peer left"})");
        });

        await using var node = builder.Build();
        await node.StartAsync();
        output.WriteLine($"site address: {node.SiteAddress}");

        // The site's own front door, separate from the node's beacon port: a vessel accepted here
        // is served as the *site*, which is the distinction #2 turned on.
        using var stopping = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await using var host = new ShrineVesselHost(node, new IPEndPoint(IPAddress.Loopback, 0));
        host.Start(stopping.Token);
        output.WriteLine($"site listening on {host.LocalEndPoint}");

        var link = new NodestarLinkProvider(node.Node, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(1))
            .Current().Link;
        Assert.True(IntonationUri.TryParse(link, out var intonation, out _), "the node's link should parse");
        Assert.True(intonation.Shrine.HasValue, "the link should advertise the site it hosts");

        var vessel = await TcpVessel.ConnectAsync("127.0.0.1", host.LocalEndPoint.Port);

        // The SITE's Signet, not the node's InviterSigil. Pinning the node succeeds into a session
        // with no Shrine behind it, and every rite on it then answers with a closed stream.
        await using var shrine = await Pilgrimage
            .OverVesselAsync(vessel, intonation.Shrine!.Value, intonation.Network, new BouncyCastleSuite())
            .WaitAsync(TimeSpan.FromSeconds(30));

        output.WriteLine($"pilgrim: pilgrimage complete, MaxPayloadBytes={shrine.Conduits.MaxPayloadBytes}");
        Assert.True(shrine.Conduits.MaxPayloadBytes > 0, "a Pilgrim should be able to read its own frame ceiling");

        await shrine.Conduits.SendAsync(new ConduitFrame
        {
            ProtocolId = BanterConduit.ProtocolId,
            SchemaVersion = 1,
            Flags = 0,
            Payload = System.Text.Encoding.UTF8.GetBytes("hello conduit"),
        }).WaitAsync(TimeSpan.FromSeconds(30));

        output.WriteLine("pilgrim: frame sent");

        // Read first, with a short patience: if the node refuses the frame it answers with a
        // sealed one, and that is a different fault from silence.
        var reply = await shrine.Conduits.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(15));
        Assert.NotNull(reply);
        output.WriteLine($"pilgrim: reply sealed={reply.IsSealed} reason={reply.SealReason} " +
                         $"protocol={reply.ProtocolId:x} payload={System.Text.Encoding.UTF8.GetString(reply.Payload ?? [])}");

        Assert.False(reply.IsSealed, $"the site sealed us: {reply.SealReason}");
        Assert.Equal("hello conduit", await arrived.Task.WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.Equal("HELLO CONDUIT", System.Text.Encoding.UTF8.GetString(reply.Payload!));
    }
}
