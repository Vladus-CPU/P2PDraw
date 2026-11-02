using System.Net;
using System.Text.Json;
using P2PDraw.Core.Models;
using P2PDraw.Core.Operations;
using P2PDraw.Core.Sync;
using P2PDraw.Networking.Transport;
using Xunit;

namespace P2PDraw.Tests.Networking;

public class LayerPeerTests
{
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task LayerChangesAndOfflineClearingConvergeAcrossMesh(int count)
    {
        var engines = new List<SyncEngine>();
        var peers = new List<TcpPeer>();
        var layers = new List<Guid>();
        var shapes = new List<Guid>();
        Guid sessionId = Guid.NewGuid();
        var cancellationToken = TestContext.Current.CancellationToken;
        try
        {
            for (int index = 0; index < count; index++)
            {
                var engine = new SyncEngine(Guid.NewGuid(), sessionId);
                var layer = engine.AddLayer($"Шар {index + 2}", index + 1);
                var shape = engine.AddShape(ShapeKind.Rectangle, new(10, 20), new(30, 40), 0xFF112233, 3, layerId: layer.LayerId);
                var peer = new TcpPeer(engine);
                peer.Start(0, IPAddress.Loopback);
                engines.Add(engine);
                peers.Add(peer);
                layers.Add(layer.LayerId);
                shapes.Add(shape.ShapeId);
            }
            for (int index = 1; index < count; index++)
            {
                await peers[index].ConnectAsync("127.0.0.1", peers[0].Port, cancellationToken);
            }
            await WaitUntilAsync(() => peers.All(peer => peer.ConnectionCount == count - 1)
                && engines.All(engine => engine.GetOperations().Length == count * 2));

            var changes = new List<Operation>();
            for (int index = 0; index < count; index++)
            {
                changes.Add(engines[index].RenameLayer(layers[0], $"Назва від {index}"));
                changes.Add(engines[index].SetLayerPosition(layers[0], index + 10));
            }
            for (int index = 0; index < count; index++)
            {
                await peers[index].SendOperationAsync(changes[index * 2], cancellationToken);
                await peers[index].SendOperationAsync(changes[index * 2 + 1], cancellationToken);
                await peers[index].SendOperationAsync(changes[index * 2], cancellationToken);
            }
            await WaitUntilAsync(() => engines.All(engine => engine.GetOperations().Length == count * 4));
            AssertConverged(engines);

            peers[0].DisconnectAll();
            await WaitUntilAsync(() => peers[0].ConnectionCount == 0 && peers[1].ConnectionCount == count - 2);
            engines[0].ClearLayer(layers[0]);
            var concurrent = engines[1].AddShape(ShapeKind.Ellipse, new(50, 60), new(70, 80), 0, 2, layerId: layers[0]);
            await peers[1].SendOperationAsync(concurrent, cancellationToken);
            await peers[0].ConnectAsync("127.0.0.1", peers[1].Port, cancellationToken);
            await WaitUntilAsync(() => engines.All(engine => engine.GetOperations().Length == count * 4 + 2));
            AssertConverged(engines);
            Assert.Contains(engines[0].GetShapes(), shape => shape.ShapeId == concurrent.ShapeId);
            Assert.Contains(shapes[0], engines[0].GetTombstones());

            var reassigned = engines[0].MoveObjectToLayer(concurrent.ShapeId, layers[1]);
            await peers[0].SendOperationAsync(reassigned, cancellationToken);
            await WaitUntilAsync(() => engines.All(engine => engine.GetOperations().Length == count * 4 + 3));
            peers[0].DisconnectAll();
            await WaitUntilAsync(() => peers[0].ConnectionCount == 0 && peers[1].ConnectionCount == count - 2);
            engines[0].MoveObject(concurrent.ShapeId, new(100, 200));
            var removedLayer = engines[1].RemoveLayer(layers[1]);
            await peers[1].SendOperationAsync(removedLayer, cancellationToken);
            await peers[0].ConnectAsync("127.0.0.1", peers[1].Port, cancellationToken);
            await WaitUntilAsync(() => engines.All(engine => engine.GetOperations().Length == count * 4 + 5));
            AssertConverged(engines);
            Assert.DoesNotContain(engines[0].GetShapes(), shape => shape.ShapeId == concurrent.ShapeId);
            Assert.DoesNotContain(engines[0].GetLayers(), layer => layer.LayerId == layers[1]);

            var cleared = engines[0].ClearCanvas();
            await peers[0].SendOperationAsync(cleared, cancellationToken);
            await WaitUntilAsync(() => engines.All(engine => engine.GetOperations().Length == count * 4 + 6));
            AssertConverged(engines);
            Assert.All(engines, engine => Assert.Empty(engine.GetShapes()));
        }
        finally
        {
            foreach (var peer in peers)
            {
                await peer.DisposeAsync();
            }
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
                snapshot.Strokes, snapshot.Shapes, snapshot.Layers, snapshot.LayerTombstones,
                snapshot.Tombstones, snapshot.PendingStrokeIds, snapshot.PendingLayerIds, snapshot.Operations
            });
            expected ??= actual;
            Assert.Equal(expected, actual);
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
