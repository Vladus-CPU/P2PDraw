using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using P2PDraw.Networking.Discovery;
using P2PDraw.Networking.Transport;
using Xunit;

namespace P2PDraw.Tests.Networking;

public class SessionDiscoveryTests
{
    [Fact]
    public async Task BrowserFindsOtherSessionsWithoutAnnouncingItself()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        var foreignSession = Guid.NewGuid();
        var announcedPeer = Guid.NewGuid();
        await using var browser = new PeerDiscovery(Guid.NewGuid(), Guid.NewGuid(), includeOtherSessions: true);
        browser.StartBrowsing(0, [IPAddress.Loopback]);
        using var sender = new UdpClient(AddressFamily.InterNetwork);
        var target = new IPEndPoint(IPAddress.Loopback, browser.Port);
        var announcement = new DiscoveryAnnouncement(announcedPeer, foreignSession, 5000, TcpPeer.ProtocolVersion, "Навчальна сесія");
        byte[] data = JsonSerializer.SerializeToUtf8Bytes(announcement);
        await sender.SendAsync(data, target, timeout.Token);
        await sender.SendAsync(data, target, timeout.Token);
        while (browser.GetPeers().IsEmpty)
        {
            await Task.Delay(10, timeout.Token);
        }

        var found = Assert.Single(browser.GetPeers());
        Assert.Equal(foreignSession, found.SessionId);
        Assert.Equal(announcedPeer, found.PeerId);
        Assert.Equal("Навчальна сесія", found.DisplayName);
    }
}
