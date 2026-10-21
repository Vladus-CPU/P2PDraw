using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using P2PDraw.Core;
using P2PDraw.Networking;
using Xunit;

namespace P2PDraw.Tests;

public class TcpPeerTests
{
    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    [Fact]
    public async Task TwoPeersExchangeConcurrentStrokesRemovalAndDuplicates()
    {
        var session = Guid.NewGuid();
        var a = new SyncEngine(Guid.NewGuid(), session);
        var b = new SyncEngine(Guid.NewGuid(), session);
        await using var first = new TcpPeer(a);
        await using var second = new TcpPeer(b);
        first.Start(0, IPAddress.Loopback);
        second.Start(0, IPAddress.Loopback);
        await first.ConnectAsync("127.0.0.1", second.Port, TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => first.ConnectionCount == 1 && second.ConnectionCount == 1);
        var aBegin = a.BeginStroke(0xFF000000, 2);
        var bBegin = b.BeginStroke(0xFFFF0000, 3);
        Operation[] aOperations = [aBegin, a.AppendStroke(aBegin.StrokeId, [new(1, 2), new(3, 4)]), a.EndStroke(aBegin.StrokeId)];
        Operation[] bOperations = [bBegin, b.AppendStroke(bBegin.StrokeId, [new(5, 6)]), b.EndStroke(bBegin.StrokeId)];
        await Task.WhenAll(aOperations.Reverse().Select(op => first.SendOperationAsync(op, TestContext.Current.CancellationToken))
            .Concat(bOperations.Reverse().Select(op => second.SendOperationAsync(op, TestContext.Current.CancellationToken))));
        await WaitUntilAsync(() => a.GetOperations().Length == 6 && b.GetOperations().Length == 6);
        await first.SendOperationAsync(aBegin, TestContext.Current.CancellationToken);
        var remove = b.RemoveStroke(aBegin.StrokeId);
        await second.SendOperationAsync(remove, TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => a.GetTombstones().Length == 1);
        Assert.Equal(7, a.GetOperations().Length);
        Assert.Equal(JsonSerializer.Serialize(a.GetStrokes()), JsonSerializer.Serialize(b.GetStrokes()));
        Assert.Equal(a.GetTombstones(), b.GetTombstones());
        Assert.Equal(JsonSerializer.Serialize(a.GetOperations()), JsonSerializer.Serialize(b.GetOperations()));
    }

    [Theory]
    [InlineData("session")]
    [InlineData("version")]
    [InlineData("self")]
    [InlineData("empty")]
    public async Task InvalidHelloIsRejected(string kind)
    {
        var engine = new SyncEngine(Guid.NewGuid(), Guid.NewGuid());
        await using var peer = new TcpPeer(engine);
        peer.Start(0, IPAddress.Loopback);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, peer.Port, TestContext.Current.CancellationToken);
        Guid id = kind == "self" ? engine.PeerId : kind == "empty" ? Guid.Empty : Guid.NewGuid();
        var hello = new HelloMessage(id, kind == "session" ? Guid.NewGuid() : engine.SessionId, kind == "version" ? 999 : TcpPeer.ProtocolVersion);
        await MessageFraming.WriteAsync<NetworkMessage>(client.GetStream(), hello, TestContext.Current.CancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        Assert.Null(await MessageFraming.ReadAsync<NetworkMessage>(client.GetStream(), timeout.Token));
        Assert.Equal(0, peer.ConnectionCount);
    }

    [Fact]
    public async Task SimultaneousConnectionsKeepOneWorkingSocketPerPeer()
    {
        var session = Guid.NewGuid();
        var a = new SyncEngine(Guid.NewGuid(), session);
        var b = new SyncEngine(Guid.NewGuid(), session);
        await using var first = new TcpPeer(a);
        await using var second = new TcpPeer(b);
        first.Start(0, IPAddress.Loopback);
        second.Start(0, IPAddress.Loopback);
        await Task.WhenAll(first.ConnectAsync("127.0.0.1", second.Port, TestContext.Current.CancellationToken), second.ConnectAsync("127.0.0.1", first.Port, TestContext.Current.CancellationToken));
        await first.ConnectAsync("127.0.0.1", second.Port, TestContext.Current.CancellationToken);
        await first.SendOperationAsync(a.BeginStroke(0, 1), TestContext.Current.CancellationToken);
        await second.SendOperationAsync(b.BeginStroke(0, 1), TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => a.GetStrokes().Length == 2 && b.GetStrokes().Length == 2);
        Assert.Equal(1, first.ConnectionCount);
        Assert.Equal(1, second.ConnectionCount);
    }

    [Fact]
    public async Task BrokenClientDoesNotStopListener()
    {
        var session = Guid.NewGuid();
        await using var server = new TcpPeer(new SyncEngine(Guid.NewGuid(), session));
        server.Start(0, IPAddress.Loopback);
        using (var broken = new TcpClient())
        {
            await broken.ConnectAsync(IPAddress.Loopback, server.Port, TestContext.Current.CancellationToken);
            await broken.GetStream().WriteAsync(new byte[] { 0, 0 }, TestContext.Current.CancellationToken);
        }
        await using var client = new TcpPeer(new SyncEngine(Guid.NewGuid(), session));
        await client.ConnectAsync("127.0.0.1", server.Port, TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => server.ConnectionCount == 1);
    }
}
