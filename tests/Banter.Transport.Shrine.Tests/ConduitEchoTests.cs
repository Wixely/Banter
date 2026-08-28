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
/// </summary>
public sealed class ConduitEchoTests(ITestOutputHelper output)
{
    private static int FreePort()
    {
        using var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        var port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    [Fact(Skip = "Blocked on CupriNodestar#2: a conduit opened over a TCP vessel is closed as soon as the pilgrimage completes, and the site's OnSession handler is never invoked. The test is the reproduction — unskip when the conduit is routed on that path.")]
    public async Task AFrameSentByAPilgrimComesBackFromTheSite()
    {
        var root = Path.Combine(Path.GetTempPath(), "banter-echo-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(root);
        var port = FreePort();

        var builder = NodestarApplication.CreateBuilder([]);
        builder.Node.Concordium = "banter-echo-test";
        builder.Node.DataDirectory = Path.Combine(root, "mesh");
        builder.Node.ListenAddress = "127.0.0.1";
        builder.Node.ListenPort = port;
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
        output.WriteLine("site: OnSession registered before Build");

        var link = new NodestarLinkProvider(node.Node, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(1))
            .Current().Link;
        Assert.True(IntonationUri.TryParse(link, out var intonation, out _), "the node's link should parse");
        output.WriteLine($"link shrine: {intonation.ShrineAddress}");

        var vessel = await TcpVessel.ConnectAsync("127.0.0.1", port);
        await using var shrine = await Pilgrimage
            .OverVesselAsync(vessel, intonation.InviterSigil, intonation.Network, new BouncyCastleSuite())
            .WaitAsync(TimeSpan.FromSeconds(30));

        output.WriteLine("pilgrim: pilgrimage complete");

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
