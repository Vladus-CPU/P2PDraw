using System.Net;
using System.Text.Json;
using P2PDraw.Core.Models;
using P2PDraw.Core.Operations;
using P2PDraw.Core.Sync;
using P2PDraw.Networking.Transport;
using Xunit;

namespace P2PDraw.Tests.Networking;

public class VectorPeerTests
{
    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private static void AssertMatchingDocuments(SyncEngine expected, SyncEngine actual)
    {
        var expectedSnapshot = expected.CreateSnapshot();
        var actualSnapshot = actual.CreateSnapshot();
        var expectedShapes = JsonSerializer.SerializeToElement(expectedSnapshot.Shapes);
        var actualShapes = JsonSerializer.SerializeToElement(actualSnapshot.Shapes);
        Assert.True(JsonElement.DeepEquals(expectedShapes, actualShapes));
        var expectedStrokes = JsonSerializer.SerializeToElement(expectedSnapshot.Strokes);
        var actualStrokes = JsonSerializer.SerializeToElement(actualSnapshot.Strokes);
        Assert.True(JsonElement.DeepEquals(expectedStrokes, actualStrokes));
        Assert.Equal(expectedSnapshot.Tombstones, actualSnapshot.Tombstones);
        Assert.Equal(expectedSnapshot.PendingStrokeIds, actualSnapshot.PendingStrokeIds);
        var expectedOperationIds = expectedSnapshot.Operations.Select(operation => operation.Stamp.OperationId);
        var actualOperationIds = actualSnapshot.Operations.Select(operation => operation.Stamp.OperationId);
        Assert.Equal(expectedOperationIds, actualOperationIds);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task ConcurrentShapeCreationAndMovesConvergeAcrossMesh(int count)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
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
            for (int index = 1; index < count; index++)
            {
                await peers[index].ConnectAsync("127.0.0.1", peers[0].Port, cancellationToken);
            }
            await WaitUntilAsync(() => peers.All(peer => peer.ConnectionCount == count - 1));

            var shapes = new List<ShapeAdd>();
            for (int index = 0; index < count; index++)
            {
                var kind = (ShapeKind)(index % 3);
                shapes.Add(engines[index].AddShape(kind, new(index, index + 1), new(index + 10, index + 20), 0xFF112233, 2));
            }
            var sending = new List<Task>();
            for (int index = 0; index < count; index++)
            {
                sending.Add(peers[index].SendOperationAsync(shapes[index], cancellationToken));
            }
            await Task.WhenAll(sending);
            await WaitUntilAsync(() => engines.All(engine => engine.GetOperations().Length == count));
            Assert.All(engines, engine => Assert.Equal(count, engine.GetShapes().Length));

            var moves = new List<ObjectMove>();
            for (int index = 0; index < count; index++)
            {
                moves.Add(engines[index].MoveObject(shapes[0].ShapeId, new(index * 10 + 5, index * 20 + 7)));
            }
            sending.Clear();
            for (int index = count - 1; index >= 0; index--)
            {
                sending.Add(peers[index].SendOperationAsync(moves[index], cancellationToken));
                sending.Add(peers[index].SendOperationAsync(moves[index], cancellationToken));
            }
            await Task.WhenAll(sending);
            await WaitUntilAsync(() => engines.All(engine => engine.GetOperations().Length == count * 2));
            var winner = moves.MaxBy(move => move.Stamp)!;
            foreach (var engine in engines)
            {
                var moved = engine.GetShapes().Single(shape => shape.ShapeId == shapes[0].ShapeId);
                Assert.Equal(winner.Offset, moved.Offset);
                Assert.Equal(winner.Stamp, moved.MovedAt);
                AssertMatchingDocuments(engines[0], engine);
            }

            peers[0].DisconnectAll();
            await WaitUntilAsync(() => peers[0].ConnectionCount == 0 && peers[1].ConnectionCount == count - 2);
            var removed = engines[0].RemoveObject(shapes[0].ShapeId);
            var lateMove = engines[1].MoveObject(shapes[0].ShapeId, new(80, 90));
            await peers[0].SendOperationAsync(removed, cancellationToken);
            await peers[1].SendOperationAsync(lateMove, cancellationToken);
            Assert.Equal(0, peers[0].ConnectionCount);
            await peers[0].ConnectAsync("127.0.0.1", peers[1].Port, cancellationToken);
            await WaitUntilAsync(() => engines.All(engine => engine.GetOperations().Length == count * 2 + 2));
            foreach (var engine in engines)
            {
                Assert.DoesNotContain(engine.GetShapes(), shape => shape.ShapeId == shapes[0].ShapeId);
                Assert.Contains(shapes[0].ShapeId, engine.GetTombstones());
                AssertMatchingDocuments(engines[0], engine);
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

    [Fact]
    public async Task OfflineClearPreservesNewShapeAndSnapshotRestoresGeometry()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var first = new SyncEngine(Guid.NewGuid(), Guid.NewGuid());
        var second = new SyncEngine(Guid.NewGuid(), first.SessionId);
        await using var firstPeer = new TcpPeer(first);
        await using var secondPeer = new TcpPeer(second);
        firstPeer.Start(0, IPAddress.Loopback);
        secondPeer.Start(0, IPAddress.Loopback);
        for (int index = 0; index < 3; index++)
        {
            first.AddShape((ShapeKind)index, new(10, 20), new(30, 40), 0xFF112233, 2, 0xFF445566);
        }
        await secondPeer.ConnectAsync("127.0.0.1", firstPeer.Port, cancellationToken);
        await WaitUntilAsync(() => second.GetOperations().Length == 3);
        AssertMatchingDocuments(first, second);

        firstPeer.DisconnectAll();
        await WaitUntilAsync(() => firstPeer.ConnectionCount == 0 && secondPeer.ConnectionCount == 0);
        first.ClearCanvas();
        var concurrent = second.AddShape(ShapeKind.Ellipse, new(100, 120), new(150, 180), 0xFF010203, 5, 0xFF334455);
        var move = second.MoveObject(concurrent.ShapeId, new(7, 9));
        Assert.Equal(0, firstPeer.ConnectionCount);
        Assert.Equal(0, secondPeer.ConnectionCount);
        await firstPeer.ConnectAsync("127.0.0.1", secondPeer.Port, cancellationToken);
        await WaitUntilAsync(() => first.GetOperations().Length == 6 && second.GetOperations().Length == 6);
        Assert.Equal(concurrent.ShapeId, Assert.Single(first.GetShapes()).ShapeId);
        Assert.Equal(3, first.GetTombstones().Length);
        AssertMatchingDocuments(first, second);

        var restored = new SyncEngine(Guid.NewGuid(), first.SessionId);
        await using var restoredPeer = new TcpPeer(restored);
        restoredPeer.Start(0, IPAddress.Loopback);
        await restoredPeer.ConnectAsync("127.0.0.1", secondPeer.Port, cancellationToken);
        await WaitUntilAsync(() => restored.GetOperations().Length == 6);
        var shape = Assert.Single(restored.GetShapes());
        Assert.Equal(concurrent.Start, shape.Start);
        Assert.Equal(concurrent.End, shape.End);
        Assert.Equal(concurrent.Color, shape.Color);
        Assert.Equal(concurrent.Width, shape.Width);
        Assert.Equal(concurrent.Fill, shape.Fill);
        Assert.Equal(move.Offset, shape.Offset);
        AssertMatchingDocuments(first, restored);
    }
}
