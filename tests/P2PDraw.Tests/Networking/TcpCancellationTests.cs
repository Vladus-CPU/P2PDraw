using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Net;
using System.Net.Sockets;
using P2PDraw.Core.Operations;
using P2PDraw.Core.Sync;
using P2PDraw.Networking.Protocol;
using P2PDraw.Networking.Transport;
using Xunit;

namespace P2PDraw.Tests.Networking;

public class TcpCancellationTests
{
    [Fact]
    public async Task CancelledSendWithoutConnectionsThrows()
    {
        var engine = new SyncEngine(Guid.NewGuid(), Guid.NewGuid());
        var operation = engine.BeginStroke(0, 1);
        await using var peer = new TcpPeer(engine);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => peer.SendOperationAsync(operation, cancellation.Token));
    }

    [Fact]
    public async Task CancelledSendDoesNotCloseHealthyConnection()
    {
        var first = new SyncEngine(Guid.NewGuid(), Guid.NewGuid());
        var second = new SyncEngine(Guid.NewGuid(), first.SessionId);
        var author = new SyncEngine(Guid.NewGuid(), first.SessionId);
        var operation = author.BeginStroke(0, 1);
        await using var firstPeer = new TcpPeer(first);
        await using var secondPeer = new TcpPeer(second);
        secondPeer.Start(0, IPAddress.Loopback);
        await firstPeer.ConnectAsync("127.0.0.1", secondPeer.Port, TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => firstPeer.ConnectionCount == 1 && secondPeer.ConnectionCount == 1);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => firstPeer.SendOperationAsync(operation, cancellation.Token));

        Assert.Equal(1, firstPeer.ConnectionCount);
        Assert.Equal(1, secondPeer.ConnectionCount);
        Assert.Empty(second.GetOperations());
        await firstPeer.SendOperationAsync(operation, TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => second.GetOperations().Length == 1);
        Assert.Equal(operation.StrokeId, Assert.Single(second.GetStrokes()).StrokeId);
    }

    [Fact]
    public async Task CancellationDuringPartialFrameThrowsAndClosesConnection()
    {
        var engine = new SyncEngine(Guid.NewGuid(), Guid.NewGuid());
        await using var peer = new TcpPeer(engine);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Server.ReceiveBufferSize = 1024;
        listener.Start();
        try
        {
            var connecting = peer.ConnectAsync("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, timeout.Token);
            using var remote = await listener.AcceptTcpClientAsync(timeout.Token);
            var stream = remote.GetStream();
            Assert.IsType<HelloMessage>(await MessageFraming.ReadAsync<NetworkMessage>(stream, timeout.Token));
            var welcome = new WelcomeMessage(Guid.NewGuid(), engine.SessionId, TcpPeer.ProtocolVersion);
            await MessageFraming.WriteAsync<NetworkMessage>(stream, welcome, timeout.Token);
            await connecting;
            Assert.IsType<SyncRequestMessage>(await MessageFraming.ReadAsync<NetworkMessage>(stream, timeout.Token));
            await MessageFraming.WriteAsync<NetworkMessage>(stream, new SyncCompleteMessage(), timeout.Token);
            var ids = ImmutableArray.CreateBuilder<Guid>(100000);
            for (int index = 0; index < 100000; index++)
            {
                ids.Add(Guid.NewGuid());
            }
            var operation = new CanvasClear(new OperationStamp(Guid.NewGuid(), engine.PeerId, 1, 1), ids.ToImmutable());
            Assert.True(MessageFraming.FitsInFrame<NetworkMessage>(new OperationMessage(operation)));
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);

            // A single frame can fit in the socket buffer even when the receiver stops reading.
            var sendingTasks = new List<Task>();
            for (int index = 0; index < 32; index++)
            {
                sendingTasks.Add(peer.SendOperationAsync(operation, cancellation.Token));
            }
            var header = new byte[4];
            await stream.ReadExactlyAsync(header, timeout.Token);
            Assert.True(BinaryPrimitives.ReadInt32BigEndian(header) > 3 * 1024 * 1024);
            Assert.Contains(sendingTasks, task => !task.IsCompleted);
            await cancellation.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.WhenAll(sendingTasks));
            await WaitUntilAsync(() => peer.ConnectionCount == 0);
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }
}
