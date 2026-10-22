using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using P2PDraw.Core.Operations;
using P2PDraw.Core.Sync;
using P2PDraw.Networking.Protocol;
using P2PDraw.Networking.Transport;
using Xunit;

namespace P2PDraw.Tests.Networking;

public class TcpPeerTests
{
    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    [Fact]
    public async Task InvalidLiveOperationDisconnectsClientWithoutChangingDocument()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var engine = new SyncEngine(Guid.NewGuid(), Guid.NewGuid());
        await using var server = new TcpPeer(engine);
        server.Start(0, IPAddress.Loopback);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.Port, cancellationToken);
        var stream = client.GetStream();
        var hello = new HelloMessage(Guid.NewGuid(), engine.SessionId, TcpPeer.ProtocolVersion);
        await MessageFraming.WriteAsync<NetworkMessage>(stream, hello, cancellationToken);
        Assert.IsType<WelcomeMessage>(await MessageFraming.ReadAsync<NetworkMessage>(stream, cancellationToken));
        await WaitUntilAsync(() => server.ConnectionCount == 1);
        var invalidOperation = new StrokeBegin(default, Guid.NewGuid(), 0, 1);
        await MessageFraming.WriteAsync<NetworkMessage>(stream, new OperationMessage(invalidOperation), cancellationToken);
        await WaitUntilAsync(() => server.ConnectionCount == 0);
        Assert.Empty(engine.GetOperations());
        Assert.Empty(engine.GetStrokes());
        await using var validPeer = new TcpPeer(new SyncEngine(Guid.NewGuid(), engine.SessionId));
        await validPeer.ConnectAsync("127.0.0.1", server.Port, cancellationToken);
        await WaitUntilAsync(() => server.ConnectionCount == 1);
    }

    [Theory]
    [InlineData("session")]
    [InlineData("version")]
    [InlineData("self")]
    [InlineData("empty")]
    public async Task ClientRejectsInvalidWelcome(string kind)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var engine = new SyncEngine(Guid.NewGuid(), Guid.NewGuid());
        await using var peer = new TcpPeer(engine);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var connectionTask = peer.ConnectAsync("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, cancellationToken);
            using var client = await listener.AcceptTcpClientAsync(cancellationToken);
            var stream = client.GetStream();
            Assert.IsType<HelloMessage>(await MessageFraming.ReadAsync<NetworkMessage>(stream, cancellationToken));
            Guid peerId = Guid.Empty;
            if (kind == "self")
            {
                peerId = engine.PeerId;
            }
            else if (kind != "empty")
            {
                peerId = Guid.NewGuid();
            }

            Guid sessionId = kind == "session" ? Guid.NewGuid() : engine.SessionId;
            int version = kind == "version" ? 999 : TcpPeer.ProtocolVersion;
            var welcome = new WelcomeMessage(peerId, sessionId, version);
            await MessageFraming.WriteAsync<NetworkMessage>(stream, welcome, cancellationToken);
            await Assert.ThrowsAsync<InvalidDataException>(() => connectionTask);
            Assert.Equal(0, peer.ConnectionCount);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task LargeSnapshotAndConcurrentLiveEditsConverge()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var firstEngine = new SyncEngine(Guid.NewGuid(), Guid.NewGuid());
        var secondEngine = new SyncEngine(Guid.NewGuid(), firstEngine.SessionId);
        for (int i = 0; i < 1200; i++)
        {
            var begin = firstEngine.BeginStroke(0, 1);
            firstEngine.AppendStroke(begin.StrokeId, [new(i, i), new(i + 1, i + 1)]);
            firstEngine.EndStroke(begin.StrokeId);
        }
        await using var firstPeer = new TcpPeer(firstEngine);
        await using var secondPeer = new TcpPeer(secondEngine);
        firstPeer.Start(0, IPAddress.Loopback);
        await secondPeer.ConnectAsync("127.0.0.1", firstPeer.Port, cancellationToken);
        var live = firstEngine.BeginStroke(0, 2);
        await firstPeer.SendOperationAsync(live, cancellationToken);
        var append = firstEngine.AppendStroke(live.StrokeId, [new(5, 6)]);
        await firstPeer.SendOperationAsync(append, cancellationToken);
        var end = firstEngine.EndStroke(live.StrokeId);
        await firstPeer.SendOperationAsync(end, cancellationToken);
        await WaitUntilAsync(() => secondEngine.GetOperations().Length == 3603);
        Assert.Equal(1201, secondEngine.GetStrokes().Length);
        Assert.Empty(secondEngine.GetPendingStrokeIds());
        string firstStateJson = JsonSerializer.Serialize(firstEngine.GetStrokes());
        string secondStateJson = JsonSerializer.Serialize(secondEngine.GetStrokes());
        Assert.Equal(firstStateJson, secondStateJson);
    }

    [Fact]
    public async Task DroppedConnectionReconnectsAndRecoversUnsentOperations()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var firstEngine = new SyncEngine(Guid.NewGuid(), Guid.NewGuid());
        var secondEngine = new SyncEngine(Guid.NewGuid(), firstEngine.SessionId);
        await using var firstPeer = new TcpPeer(firstEngine);
        await using var secondPeer = new TcpPeer(secondEngine);
        secondPeer.Start(0, IPAddress.Loopback);
        await firstPeer.ConnectAsync("127.0.0.1", secondPeer.Port, cancellationToken);
        await WaitUntilAsync(() => firstPeer.ConnectionCount == 1 && secondPeer.ConnectionCount == 1);
        firstPeer.DisconnectAll(reconnect: true);
        firstEngine.BeginStroke(0, 1);
        secondEngine.BeginStroke(0, 2);
        await WaitUntilAsync(() => firstEngine.GetStrokes().Length == 2 && secondEngine.GetStrokes().Length == 2);
        Assert.Equal(1, firstPeer.ConnectionCount);
        Assert.Equal(1, secondPeer.ConnectionCount);
        string firstStateJson = JsonSerializer.Serialize(firstEngine.GetStrokes());
        string secondStateJson = JsonSerializer.Serialize(secondEngine.GetStrokes());
        Assert.Equal(firstStateJson, secondStateJson);
    }

    [Fact]
    public async Task NewPeerReceivesSnapshotIncludingTombstones()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var firstEngine = new SyncEngine(Guid.NewGuid(), Guid.NewGuid());
        var secondEngine = new SyncEngine(Guid.NewGuid(), firstEngine.SessionId);
        var removed = firstEngine.BeginStroke(0, 1);
        firstEngine.RemoveStroke(removed.StrokeId);
        var visible = firstEngine.BeginStroke(0, 2);
        firstEngine.AppendStroke(visible.StrokeId, [new(1, 2)]);
        firstEngine.EndStroke(visible.StrokeId);
        await using var firstPeer = new TcpPeer(firstEngine);
        await using var secondPeer = new TcpPeer(secondEngine);
        firstPeer.Start(0, IPAddress.Loopback);
        await secondPeer.ConnectAsync("127.0.0.1", firstPeer.Port, cancellationToken);
        await WaitUntilAsync(() => secondEngine.GetOperations().Length == 5);
        string firstStateJson = JsonSerializer.Serialize(firstEngine.GetStrokes());
        string secondStateJson = JsonSerializer.Serialize(secondEngine.GetStrokes());
        Assert.Equal(firstStateJson, secondStateJson);
        Assert.Equal(firstEngine.GetTombstones(), secondEngine.GetTombstones());
        Assert.False(secondEngine.Receive(removed));
    }

    [Fact]
    public async Task ReconnectRepairsSequenceGapBelowMaximum()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var firstEngine = new SyncEngine(Guid.NewGuid(), Guid.NewGuid());
        var secondEngine = new SyncEngine(Guid.NewGuid(), firstEngine.SessionId);
        var begin = firstEngine.BeginStroke(0, 1);
        firstEngine.AppendStroke(begin.StrokeId, [new(1, 2)]);
        var end = firstEngine.EndStroke(begin.StrokeId);
        secondEngine.Receive(begin);
        secondEngine.Receive(end);
        await using var firstPeer = new TcpPeer(firstEngine);
        await using var secondPeer = new TcpPeer(secondEngine);
        firstPeer.Start(0, IPAddress.Loopback);
        await secondPeer.ConnectAsync("127.0.0.1", firstPeer.Port, cancellationToken);
        await WaitUntilAsync(() => secondEngine.GetOperations().Length == 3);
        Assert.Empty(secondEngine.GetPendingStrokeIds());
        var restoredStroke = Assert.Single(secondEngine.GetStrokes());
        Assert.True(restoredStroke.IsComplete);
        string firstStateJson = JsonSerializer.Serialize(firstEngine.GetStrokes());
        string secondStateJson = JsonSerializer.Serialize(secondEngine.GetStrokes());
        Assert.Equal(firstStateJson, secondStateJson);
    }

    [Fact]
    public async Task WireSyncSendsOnlyMissingOperationIncludingGap()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var engine = new SyncEngine(Guid.NewGuid(), Guid.NewGuid());
        var begin = engine.BeginStroke(0, 1);
        var append = engine.AppendStroke(begin.StrokeId, [new(1, 2)]);
        var end = engine.EndStroke(begin.StrokeId);
        await using var server = new TcpPeer(engine);
        server.Start(0, IPAddress.Loopback);
        using var client = new TcpClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        await client.ConnectAsync(IPAddress.Loopback, server.Port, timeout.Token);
        var stream = client.GetStream();
        await MessageFraming.WriteAsync<NetworkMessage>(stream, new HelloMessage(Guid.NewGuid(), engine.SessionId, TcpPeer.ProtocolVersion), timeout.Token);
        Assert.IsType<WelcomeMessage>(await MessageFraming.ReadAsync<NetworkMessage>(stream, timeout.Token));
        await MessageFraming.WriteAsync<NetworkMessage>(stream, new SyncRequestMessage([begin.Stamp.OperationId, end.Stamp.OperationId]), timeout.Token);
        var receivedOperations = new List<Operation>();
        while (true)
        {
            var message = await MessageFraming.ReadAsync<NetworkMessage>(stream, timeout.Token);
            if (message is null)
            {
                break;
            }

            if (message is SyncCompleteMessage)
            {
                break;
            }

            Assert.IsNotType<SnapshotMessage>(message);
            if (message is OperationMessage operation)
            {
                receivedOperations.Add(operation.Operation);
            }
        }
        Assert.Equal(append.Stamp.OperationId, Assert.Single(receivedOperations).Stamp.OperationId);
    }

    [Fact]
    public async Task TwoPeersExchangeConcurrentStrokesRemovalAndDuplicates()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var session = Guid.NewGuid();
        var firstEngine = new SyncEngine(Guid.NewGuid(), session);
        var secondEngine = new SyncEngine(Guid.NewGuid(), session);
        await using var firstPeer = new TcpPeer(firstEngine);
        await using var secondPeer = new TcpPeer(secondEngine);
        firstPeer.Start(0, IPAddress.Loopback);
        secondPeer.Start(0, IPAddress.Loopback);
        await firstPeer.ConnectAsync("127.0.0.1", secondPeer.Port, cancellationToken);
        await WaitUntilAsync(() => firstPeer.ConnectionCount == 1 && secondPeer.ConnectionCount == 1);
        var firstBegin = firstEngine.BeginStroke(0xFF000000, 2);
        var secondBegin = secondEngine.BeginStroke(0xFFFF0000, 3);
        var firstAppend = firstEngine.AppendStroke(firstBegin.StrokeId, [new(1, 2), new(3, 4)]);
        var firstEnd = firstEngine.EndStroke(firstBegin.StrokeId);
        var secondAppend = secondEngine.AppendStroke(secondBegin.StrokeId, [new(5, 6)]);
        var secondEnd = secondEngine.EndStroke(secondBegin.StrokeId);
        Operation[] firstOperations = [firstBegin, firstAppend, firstEnd];
        Operation[] secondOperations = [secondBegin, secondAppend, secondEnd];

        var sendingTasks = new List<Task>();
        foreach (var operation in firstOperations.Reverse())
        {
            sendingTasks.Add(firstPeer.SendOperationAsync(operation, cancellationToken));
        }

        foreach (var operation in secondOperations.Reverse())
        {
            sendingTasks.Add(secondPeer.SendOperationAsync(operation, cancellationToken));
        }

        await Task.WhenAll(sendingTasks);
        await WaitUntilAsync(() => firstEngine.GetOperations().Length == 6 && secondEngine.GetOperations().Length == 6);
        await firstPeer.SendOperationAsync(firstBegin, cancellationToken);
        var remove = secondEngine.RemoveStroke(firstBegin.StrokeId);
        await secondPeer.SendOperationAsync(remove, cancellationToken);
        await WaitUntilAsync(() => firstEngine.GetTombstones().Length == 1);
        Assert.Equal(7, firstEngine.GetOperations().Length);
        string firstStateJson = JsonSerializer.Serialize(firstEngine.GetStrokes());
        string secondStateJson = JsonSerializer.Serialize(secondEngine.GetStrokes());
        Assert.Equal(firstStateJson, secondStateJson);
        Assert.Equal(firstEngine.GetTombstones(), secondEngine.GetTombstones());
        string firstLogJson = JsonSerializer.Serialize(firstEngine.GetOperations());
        string secondLogJson = JsonSerializer.Serialize(secondEngine.GetOperations());
        Assert.Equal(firstLogJson, secondLogJson);
    }

    [Theory]
    [InlineData("session")]
    [InlineData("version")]
    [InlineData("self")]
    [InlineData("empty")]
    public async Task InvalidHelloIsRejected(string kind)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var engine = new SyncEngine(Guid.NewGuid(), Guid.NewGuid());
        await using var peer = new TcpPeer(engine);
        peer.Start(0, IPAddress.Loopback);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, peer.Port, cancellationToken);
        Guid peerId = Guid.Empty;
        if (kind == "self")
        {
            peerId = engine.PeerId;
        }
        else if (kind != "empty")
        {
            peerId = Guid.NewGuid();
        }

        Guid sessionId = kind == "session" ? Guid.NewGuid() : engine.SessionId;
        int version = kind == "version" ? 999 : TcpPeer.ProtocolVersion;
        var hello = new HelloMessage(peerId, sessionId, version);
        await MessageFraming.WriteAsync<NetworkMessage>(client.GetStream(), hello, cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        Assert.Null(await MessageFraming.ReadAsync<NetworkMessage>(client.GetStream(), timeout.Token));
        Assert.Equal(0, peer.ConnectionCount);
    }

    [Fact]
    public async Task SimultaneousConnectionsKeepOneWorkingSocketPerPeer()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var session = Guid.NewGuid();
        var firstEngine = new SyncEngine(Guid.NewGuid(), session);
        var secondEngine = new SyncEngine(Guid.NewGuid(), session);
        await using var firstPeer = new TcpPeer(firstEngine);
        await using var secondPeer = new TcpPeer(secondEngine);
        firstPeer.Start(0, IPAddress.Loopback);
        secondPeer.Start(0, IPAddress.Loopback);
        var firstConnection = firstPeer.ConnectAsync("127.0.0.1", secondPeer.Port, cancellationToken);
        var secondConnection = secondPeer.ConnectAsync("127.0.0.1", firstPeer.Port, cancellationToken);
        await Task.WhenAll(firstConnection, secondConnection);
        await firstPeer.ConnectAsync("127.0.0.1", secondPeer.Port, cancellationToken);
        var firstBegin = firstEngine.BeginStroke(0, 1);
        await firstPeer.SendOperationAsync(firstBegin, cancellationToken);
        var secondBegin = secondEngine.BeginStroke(0, 1);
        await secondPeer.SendOperationAsync(secondBegin, cancellationToken);
        await WaitUntilAsync(() => firstEngine.GetStrokes().Length == 2 && secondEngine.GetStrokes().Length == 2);
        Assert.Equal(1, firstPeer.ConnectionCount);
        Assert.Equal(1, secondPeer.ConnectionCount);
    }

    [Fact]
    public async Task BrokenClientDoesNotStopListener()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var session = Guid.NewGuid();
        await using var server = new TcpPeer(new SyncEngine(Guid.NewGuid(), session));
        server.Start(0, IPAddress.Loopback);
        using (var brokenClient = new TcpClient())
        {
            await brokenClient.ConnectAsync(IPAddress.Loopback, server.Port, cancellationToken);
            await brokenClient.GetStream().WriteAsync(new byte[] { 0, 0 }, cancellationToken);
        }
        await using var client = new TcpPeer(new SyncEngine(Guid.NewGuid(), session));
        await client.ConnectAsync("127.0.0.1", server.Port, cancellationToken);
        await WaitUntilAsync(() => server.ConnectionCount == 1);
    }
}
