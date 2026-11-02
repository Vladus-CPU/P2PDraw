using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using P2PDraw.Core.Sync;
using P2PDraw.Networking.Protocol;
using P2PDraw.Networking.Transport;
using Xunit;

namespace P2PDraw.Tests.Networking;

public class TcpMeshTests
{
    [Fact]
    public async Task InvalidFrameDisconnectsWithoutCrashingListener()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var engine = new SyncEngine(Guid.NewGuid(), Guid.NewGuid());
        await using var server = new TcpPeer(engine);
        server.Start(0, IPAddress.Loopback);
        using var remote = new TcpClient();
        await remote.ConnectAsync(IPAddress.Loopback, server.Port, cancellationToken);
        var stream = remote.GetStream();
        await MessageFraming.WriteAsync<NetworkMessage>(stream, new HelloMessage(Guid.NewGuid(), engine.SessionId, TcpPeer.ProtocolVersion), cancellationToken);
        Assert.IsType<WelcomeMessage>(await MessageFraming.ReadAsync<NetworkMessage>(stream, cancellationToken));
        await WaitUntilAsync(() => server.ConnectionCount == 1);

        await stream.WriteAsync(new byte[4], cancellationToken);
        await WaitUntilAsync(() => server.ConnectionCount == 0);
        Assert.Empty(engine.GetOperations());

        await using var validPeer = new TcpPeer(new SyncEngine(Guid.NewGuid(), engine.SessionId));
        await validPeer.ConnectAsync("127.0.0.1", server.Port, cancellationToken);
        await WaitUntilAsync(() => server.ConnectionCount == 1 && validPeer.ConnectionCount == 1);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public async Task JoiningOneEndpointFormsMeshWithoutDiscovery(int count)
    {
        var sessionId = Guid.NewGuid();
        var engines = new List<SyncEngine>();
        var peers = new List<TcpPeer>();
        try
        {
            for (int index = 0; index < count; index++)
            {
                var engine = new SyncEngine(Guid.NewGuid(), sessionId);
                var peer = new TcpPeer(engine);
                peer.Start(0, IPAddress.Loopback);
                engines.Add(engine);
                peers.Add(peer);
            }
            var joining = new List<Task<Guid>>();
            for (int index = 1; index < count; index++)
            {
                joining.Add(peers[index].ConnectAsync("127.0.0.1", peers[0].Port, TestContext.Current.CancellationToken));
            }
            await Task.WhenAll(joining);
            await WaitUntilAsync(() => peers.All(peer => peer.ConnectionCount == count - 1));
            for (int first = 0; first < count; first++)
            {
                for (int second = 0; second < count; second++)
                {
                    Assert.Equal(first != second, peers[first].IsConnected(engines[second].PeerId));
                }
            }

            var sending = new List<Task>();
            for (int index = 0; index < count; index++)
            {
                var begin = engines[index].BeginStroke(0, index + 1);
                sending.Add(peers[index].SendOperationAsync(begin, TestContext.Current.CancellationToken));
            }
            await Task.WhenAll(sending);
            await WaitUntilAsync(() => engines.All(engine => engine.GetOperations().Length == count));

            await peers[0].DisposeAsync();
            await WaitUntilAsync(() => peers.Skip(1).All(peer => peer.ConnectionCount == count - 2));
            var existing = engines[1].GetStrokes()[0];
            var removal = engines[1].RemoveStroke(existing.StrokeId);
            await peers[1].SendOperationAsync(removal, TestContext.Current.CancellationToken);
            var added = engines[2].BeginStroke(0, 5);
            await peers[2].SendOperationAsync(added, TestContext.Current.CancellationToken);
            await WaitUntilAsync(() => engines.Skip(1).All(engine => engine.GetOperations().Length == count + 2));

            var expected = engines[1].CreateSnapshot();
            for (int index = 2; index < count; index++)
            {
                var actual = engines[index].CreateSnapshot();
                Assert.Equal(expected.Tombstones, actual.Tombstones);
                Assert.Equal(expected.PendingStrokeIds, actual.PendingStrokeIds);
                var expectedOperationIds = expected.Operations.Select(operation => operation.Stamp.OperationId);
                var actualOperationIds = actual.Operations.Select(operation => operation.Stamp.OperationId);
                Assert.Equal(expectedOperationIds, actualOperationIds);
                var expectedStrokes = JsonSerializer.SerializeToElement(expected.Strokes);
                var actualStrokes = JsonSerializer.SerializeToElement(actual.Strokes);
                Assert.True(JsonElement.DeepEquals(expectedStrokes, actualStrokes));
            }
        }
        finally
        {
            foreach (var peer in peers)
            {
                await peer.DisposeAsync();
            }
        }
    }

    [Theory]
    [InlineData("0.0.0.0", 5101)]
    [InlineData("239.255.51.10", 5101)]
    [InlineData("127.0.0.1", 0)]
    [InlineData("127.0.0.1", 65536)]
    [InlineData("invalid", 5101)]
    public async Task InvalidMembershipEndpointRejectsWelcome(string address, int port)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var engine = new SyncEngine(Guid.NewGuid(), Guid.NewGuid());
        await using var peer = new TcpPeer(engine);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var connecting = peer.ConnectAsync("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, cancellationToken);
            using var remote = await listener.AcceptTcpClientAsync(cancellationToken);
            var stream = remote.GetStream();
            Assert.IsType<HelloMessage>(await MessageFraming.ReadAsync<NetworkMessage>(stream, cancellationToken));
            var welcome = new WelcomeMessage(Guid.NewGuid(), engine.SessionId, TcpPeer.ProtocolVersion)
            {
                Peers = [new PeerEndpoint(Guid.NewGuid(), address, port)]
            };
            await MessageFraming.WriteAsync<NetworkMessage>(stream, welcome, cancellationToken);
            await Assert.ThrowsAsync<InvalidDataException>(() => connecting);
            Assert.Equal(0, peer.ConnectionCount);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task MembershipConnectionRejectsDifferentPeerIdentity()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var engine = new SyncEngine(Guid.NewGuid(), Guid.NewGuid());
        var unexpected = new SyncEngine(Guid.NewGuid(), engine.SessionId);
        await using var unexpectedPeer = new TcpPeer(unexpected);
        unexpectedPeer.Start(0, IPAddress.Loopback);
        var attempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        unexpectedPeer.Changed += () =>
        {
            if (unexpectedPeer.ConnectionCount > 0)
            {
                attempted.TrySetResult();
            }
        };
        await using var peer = new TcpPeer(engine);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var connecting = peer.ConnectAsync("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, cancellationToken);
            using var remote = await listener.AcceptTcpClientAsync(cancellationToken);
            var stream = remote.GetStream();
            Assert.IsType<HelloMessage>(await MessageFraming.ReadAsync<NetworkMessage>(stream, cancellationToken));
            var welcome = new WelcomeMessage(Guid.NewGuid(), engine.SessionId, TcpPeer.ProtocolVersion)
            {
                Peers = [new PeerEndpoint(Guid.NewGuid(), "127.0.0.1", unexpectedPeer.Port)]
            };
            await MessageFraming.WriteAsync<NetworkMessage>(stream, welcome, cancellationToken);
            await connecting;
            await attempted.Task.WaitAsync(TimeSpan.FromSeconds(8), cancellationToken);
            await WaitUntilAsync(() => unexpectedPeer.ConnectionCount == 0);
            Assert.False(peer.IsConnected(unexpected.PeerId));
            Assert.Empty(engine.GetOperations());
        }
        finally
        {
            listener.Stop();
        }
    }
}
