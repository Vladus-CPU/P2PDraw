using System.Net;
using System.Text.Json;
using P2PDraw.App.Editing;
using P2PDraw.Core.Models;
using P2PDraw.Core.Operations;
using P2PDraw.Core.Sync;
using P2PDraw.Networking.Transport;
using Xunit;

namespace P2PDraw.Tests.Integration;

public class HistoryPeerTests
{
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task UndoRedoAndOfflineRecreationConvergeAcrossMesh(int count)
    {
        var engines = new List<SyncEngine>();
        var peers = new List<TcpPeer>();
        var pending = new Queue<Operation>();
        var cancellationToken = TestContext.Current.CancellationToken;
        Guid sessionId = Guid.NewGuid();
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
            var source = engines[0];
            var history = new EditHistory(() => source, pending.Enqueue);
            var layer = source.AddLayer("Спільні фігури");
            pending.Enqueue(layer);
            var added = source.AddShape(ShapeKind.Rectangle, new(1, 2), new(30, 40), 0xFF112233, 3, layerId: layer.LayerId);
            pending.Enqueue(added);
            history.RecordCreation(added.ShapeId);
            var moved = source.MoveObject(added.ShapeId, new(10, 20));
            pending.Enqueue(moved);
            history.RecordMove(moved);
            var resized = source.ResizeShape(added.ShapeId, new(5, 6), new(70, 80));
            pending.Enqueue(resized);
            history.RecordResize(resized);
            await FlushAsync(peers[0], pending);
            await WaitUntilAsync(() => engines.All(engine => engine.GetOperations().Length == 4));
            AssertConverged(engines);

            Assert.True(history.Undo());
            await FlushAsync(peers[0], pending);
            await WaitUntilAsync(() => engines.All(engine => engine.GetOperations().Length == 5));
            Assert.All(engines, engine => Assert.Equal(added.End, Assert.Single(engine.GetShapes()).End));
            Assert.True(history.Undo());
            await FlushAsync(peers[0], pending);
            await WaitUntilAsync(() => engines.All(engine => engine.GetOperations().Length == 6));
            Assert.All(engines, engine => Assert.Equal(default, Assert.Single(engine.GetShapes()).Offset));
            Assert.True(history.Redo());
            Assert.True(history.Redo());
            await FlushAsync(peers[0], pending);
            await WaitUntilAsync(() => engines.All(engine => engine.GetOperations().Length == 8));
            Assert.All(engines, engine =>
            {
                var shape = Assert.Single(engine.GetShapes());
                Assert.Equal(moved.Offset, shape.Offset);
                Assert.Equal(resized.Start, shape.Start);
                Assert.Equal(resized.End, shape.End);
            });
            AssertConverged(engines);

            var stroke = source.BeginStroke(0xFF445566, 2, layer.LayerId);
            pending.Enqueue(stroke);
            pending.Enqueue(source.AppendStroke(stroke.StrokeId, [new(10, 20), new(30, 40)]));
            pending.Enqueue(source.EndStroke(stroke.StrokeId));
            history.RecordCreation(stroke.StrokeId);
            await FlushAsync(peers[0], pending);
            await WaitUntilAsync(() => engines.All(engine => engine.GetOperations().Length == 11));
            peers[0].DisconnectAll();
            await WaitUntilAsync(() => peers[0].ConnectionCount == 0 && peers[1].ConnectionCount == count - 2);
            Assert.True(history.Undo());
            Assert.True(history.Redo());
            await FlushAsync(peers[0], pending);
            var recreated = Assert.Single(source.GetStrokes());
            Assert.NotEqual(stroke.StrokeId, recreated.StrokeId);
            Assert.Equal(layer.LayerId, recreated.LayerId);
            Assert.Equal(new StrokePoint[] { new(10, 20), new(30, 40) }, recreated.Points);
            Assert.Contains(stroke.StrokeId, source.GetTombstones());
            var concurrent = engines[1].AddShape(ShapeKind.Ellipse, new(90, 100), new(110, 120), 0, 1, layerId: layer.LayerId);
            await peers[1].SendOperationAsync(concurrent, cancellationToken);
            int expectedOperations = source.GetOperations().Length + 1;
            await peers[0].ConnectAsync("127.0.0.1", peers[1].Port, cancellationToken);
            await WaitUntilAsync(() => engines.All(engine => engine.GetOperations().Length == expectedOperations)
                && peers.All(peer => peer.ConnectionCount == count - 1));
            AssertConverged(engines);

            await peers[^1].DisposeAsync();
            peers.RemoveAt(peers.Count - 1);
            engines.RemoveAt(engines.Count - 1);
            await WaitUntilAsync(() => peers.All(peer => peer.ConnectionCount == count - 2));
            var lateEngine = new SyncEngine(Guid.NewGuid(), sessionId);
            var latePeer = new TcpPeer(lateEngine);
            latePeer.Start(0, IPAddress.Loopback);
            engines.Add(lateEngine);
            peers.Add(latePeer);
            await latePeer.ConnectAsync("127.0.0.1", peers[0].Port, cancellationToken);
            await WaitUntilAsync(() => engines.All(engine => engine.GetOperations().Length == expectedOperations)
                && peers.All(peer => peer.ConnectionCount == count - 1));
            AssertConverged(engines);
            Assert.Contains(stroke.StrokeId, lateEngine.GetTombstones());
            Assert.Equal(recreated.StrokeId, Assert.Single(lateEngine.GetStrokes()).StrokeId);
        }
        finally
        {
            foreach (var peer in peers)
            {
                await peer.DisposeAsync();
            }
        }
    }

    private static async Task FlushAsync(TcpPeer peer, Queue<Operation> pending)
    {
        while (pending.TryDequeue(out var operation))
        {
            await peer.SendOperationAsync(operation, TestContext.Current.CancellationToken);
        }
    }

    private static void AssertConverged(IEnumerable<SyncEngine> engines)
    {
        string? expected = null;
        foreach (var engine in engines)
        {
            var snapshot = engine.CreateSnapshot();
            string actual = JsonSerializer.Serialize(new
            {
                snapshot.SessionId, snapshot.Strokes, snapshot.Shapes, snapshot.Layers, snapshot.Tombstones,
                snapshot.LayerTombstones, snapshot.PendingStrokeIds, snapshot.PendingLayerIds, snapshot.Operations
            });
            expected ??= actual;
            Assert.Equal(expected, actual);
            Assert.Empty(snapshot.PendingStrokeIds);
            Assert.Empty(snapshot.PendingLayerIds);
        }
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
}
