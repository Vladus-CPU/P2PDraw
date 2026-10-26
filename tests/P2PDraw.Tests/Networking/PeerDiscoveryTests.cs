using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using P2PDraw.Core.Sync;
using P2PDraw.Networking.Discovery;
using P2PDraw.Networking.Transport;
using Xunit;

namespace P2PDraw.Tests.Networking;

public class PeerDiscoveryTests
{
    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        while (!condition())
        {
            await Task.Delay(20, timeout.Token);
        }
    }

    [Fact]
    public async Task MulticastFindsPeerOnceConnectsByTcpAndExpires()
    {
        var first = new SyncEngine(Guid.NewGuid(), Guid.NewGuid());
        var second = new SyncEngine(Guid.NewGuid(), first.SessionId);
        await using var firstTcp = new TcpPeer(first);
        await using var secondTcp = new TcpPeer(second);
        firstTcp.Start(0, IPAddress.Loopback);
        secondTcp.Start(0, IPAddress.Loopback);
        await using var firstDiscovery = new PeerDiscovery(first.PeerId, first.SessionId);
        await using var secondDiscovery = new PeerDiscovery(second.PeerId, second.SessionId);
        firstDiscovery.Start(firstTcp.Port, 0, [IPAddress.Loopback]);
        secondDiscovery.Start(secondTcp.Port, firstDiscovery.Port, [IPAddress.Loopback]);
        await WaitUntilAsync(() => firstDiscovery.GetPeers().Length == 1 && secondDiscovery.GetPeers().Length == 1);
        var found = Assert.Single(firstDiscovery.GetPeers());
        Assert.Equal(second.PeerId, found.PeerId);
        Assert.Equal(secondTcp.Port, found.TcpPort);
        await firstTcp.ConnectAsync(found.Address, found.TcpPort, TestContext.Current.CancellationToken);
        var begin = first.BeginStroke(0, 1);
        await firstTcp.SendOperationAsync(begin, TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => second.GetStrokes().Length == 1);
        Assert.Equal(begin.StrokeId, Assert.Single(second.GetStrokes()).StrokeId);

        DateTimeOffset lastSeen = found.LastSeen;
        await WaitUntilAsync(() => firstDiscovery.GetPeers()[0].LastSeen > lastSeen);
        Assert.Single(firstDiscovery.GetPeers());
        await secondDiscovery.DisposeAsync();
        await WaitUntilAsync(() => firstDiscovery.GetPeers().IsEmpty);
        Assert.Equal(1, firstTcp.ConnectionCount);
    }

    [Fact]
    public async Task InvalidAnnouncementsDoNotEnterPeerList()
    {
        var peerId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        await using var discovery = new PeerDiscovery(peerId, sessionId);
        discovery.Start(5101, 0, [IPAddress.Loopback]);
        using var sender = new UdpClient(AddressFamily.InterNetwork);
        var endpoint = new IPEndPoint(IPAddress.Loopback, discovery.Port);
        var valid = new DiscoveryAnnouncement(Guid.NewGuid(), sessionId, 5102, TcpPeer.ProtocolVersion);
        DiscoveryAnnouncement[] invalid =
        [
            valid with { PeerId = peerId }, valid with { PeerId = Guid.Empty },
            valid with { SessionId = Guid.NewGuid() }, valid with { TcpPort = 0 },
            valid with { TcpPort = 65536 }, valid with { ProtocolVersion = 999 }
        ];
        foreach (var announcement in invalid)
        {
            await sender.SendAsync(JsonSerializer.SerializeToUtf8Bytes(announcement), endpoint, TestContext.Current.CancellationToken);
        }
        await sender.SendAsync(new byte[] { 123 }, endpoint, TestContext.Current.CancellationToken);
        await sender.SendAsync(new byte[1025], endpoint, TestContext.Current.CancellationToken);
        await sender.SendAsync(JsonSerializer.SerializeToUtf8Bytes(valid), endpoint, TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => discovery.GetPeers().Length == 1);
        Assert.Equal(valid.PeerId, Assert.Single(discovery.GetPeers()).PeerId);
    }
}
