using System.Text.Json;
using P2PDraw.Core.Models;
using P2PDraw.Core.Operations;
using P2PDraw.Core.Sync;
using Xunit;

namespace P2PDraw.Tests.Core;

public class LayerTests
{
    [Fact]
    public void DefaultLayerHasSameNonemptyIdOnEveryPeer()
    {
        var first = new SyncEngine(Guid.NewGuid(), Guid.NewGuid());
        var second = new SyncEngine(Guid.NewGuid(), first.SessionId);
        var layer = Assert.Single(first.GetLayers());
        Assert.NotEqual(Guid.Empty, layer.LayerId);
        Assert.Equal(Layer.DefaultLayerId, layer.LayerId);
        Assert.Equal("Шар 1", layer.Name);
        Assert.Equal(first.GetLayers(), second.GetLayers());
        Assert.Empty(first.GetOperations());
        Assert.Equal(layer.LayerId, first.BeginStroke(0, 1).LayerId);
    }

    [Fact]
    public void LastLayerCanBeDeletedWithoutImplicitRecreation()
    {
        var engine = new SyncEngine(Guid.NewGuid(), Guid.NewGuid());
        engine.RemoveLayer(Layer.DefaultLayerId);
        Assert.Empty(engine.GetLayers());
        Assert.Throws<InvalidOperationException>(() => engine.BeginStroke(0, 1));
        Assert.Throws<InvalidOperationException>(() => engine.AddShape(ShapeKind.Line, new(1, 2), new(3, 4), 0, 1));

        var layer = engine.AddLayer("Новий шар");
        var stroke = engine.BeginStroke(0, 1, layer.LayerId);
        Assert.Equal(layer.LayerId, Assert.Single(engine.GetLayers()).LayerId);
        Assert.Equal(layer.LayerId, stroke.LayerId);
        Assert.Equal(3, engine.GetOperations().Length);
        Assert.Equal(Layer.DefaultLayerId, Assert.Single(engine.CreateSnapshot().LayerTombstones));
    }

    [Fact]
    public void NameAndOrderAreIndependentRegistersWithStablePeerTieBreak()
    {
        var engine = new SyncEngine(Guid.NewGuid(), Guid.NewGuid());
        var add = engine.AddLayer("Початкова назва", 1);
        var lowerPeer = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var higherPeer = Guid.Parse("22222222-2222-2222-2222-222222222222");
        Operation[] operations =
        [
            add,
            new LayerRename(new(Guid.NewGuid(), lowerPeer, 1, 5), add.LayerId, "Нижчий"),
            new LayerRename(new(Guid.NewGuid(), higherPeer, 1, 5), add.LayerId, "Вищий"),
            new LayerReorder(new(Guid.NewGuid(), lowerPeer, 2, 6), add.LayerId, -2),
            new LayerReorder(new(Guid.NewGuid(), higherPeer, 2, 6), add.LayerId, -1)
        ];
        for (int seed = 0; seed < 40; seed++)
        {
            var document = new DocumentState();
            var random = new Random(seed);
            foreach (var operation in operations.OrderBy(_ => random.Next()))
            {
                Assert.True(document.Apply(operation));
                Assert.False(document.Apply(operation));
            }
            var layer = document.GetLayers()[0];
            Assert.Equal(add.LayerId, layer.LayerId);
            Assert.Equal("Вищий", layer.Name);
            Assert.Equal(-1, layer.Position);
            Assert.Equal(higherPeer, layer.NameChangedAt!.Value.PeerId);
            Assert.Equal(higherPeer, layer.OrderChangedAt!.Value.PeerId);
            Assert.Empty(document.GetPendingLayerIds());
        }
    }

    [Fact]
    public void UnknownLayerAndOrphanRenameWaitForLayerCreation()
    {
        var source = new SyncEngine(Guid.NewGuid(), Guid.NewGuid());
        var add = source.AddLayer("Шар");
        var rename = source.RenameLayer(add.LayerId, "Перейменований");
        var shape = source.AddShape(ShapeKind.Rectangle, new(1, 2), new(3, 4), 0, 1, layerId: add.LayerId);
        var receiving = new SyncEngine(Guid.NewGuid(), source.SessionId);
        receiving.Receive(shape);
        receiving.Receive(rename);
        Assert.Empty(receiving.GetShapes());
        Assert.Equal(add.LayerId, Assert.Single(receiving.GetPendingLayerIds()));
        var snapshot = JsonSerializer.Deserialize<Snapshot>(JsonSerializer.Serialize(receiving.CreateSnapshot()))!;
        var restored = new SyncEngine(Guid.NewGuid(), source.SessionId);
        restored.MergeSnapshot(snapshot);

        restored.Receive(add);

        Assert.Empty(restored.GetPendingLayerIds());
        Assert.Equal("Перейменований", restored.GetLayers()[1].Name);
        Assert.Equal(add.LayerId, Assert.Single(restored.GetShapes()).LayerId);
    }

    [Fact]
    public void DeletedLayerMasksLateEditsUntilExplicitMembershipMovesToLiveLayer()
    {
        var source = new SyncEngine(Guid.NewGuid(), Guid.NewGuid());
        var removedLayer = source.AddLayer("Видалений");
        var destination = source.AddLayer("Призначення");
        var shape = source.AddShape(ShapeKind.Ellipse, new(1, 2), new(3, 4), 0, 1, layerId: removedLayer.LayerId);
        source.RemoveLayer(removedLayer.LayerId);
        var lateMove = new ObjectMove(new(Guid.NewGuid(), Guid.NewGuid(), 1, 50), shape.ShapeId, new(20, 30));
        Operation[] operations = [.. source.GetOperations(), lateMove];
        for (int seed = 0; seed < 40; seed++)
        {
            var document = new DocumentState();
            var random = new Random(seed);
            foreach (var operation in operations.OrderBy(_ => random.Next()))
            {
                document.Apply(operation);
            }
            Assert.Empty(document.GetShapes());
            Assert.Empty(document.GetTombstones());
            Assert.Equal(removedLayer.LayerId, Assert.Single(document.GetLayerTombstones()));
            Assert.Empty(document.GetPendingLayerIds());
        }
        source.Receive(lateMove);
        Assert.Throws<InvalidOperationException>(() => source.MoveObject(shape.ShapeId, new(1, 2)));

        source.MoveObjectToLayer(shape.ShapeId, destination.LayerId);

        var rescued = Assert.Single(source.GetShapes());
        Assert.Equal(destination.LayerId, rescued.LayerId);
        Assert.Equal(lateMove.Offset, rescued.Offset);
        Assert.Equal(shape.ShapeId, rescued.ShapeId);
    }

    [Fact]
    public void ConcurrentMembershipMovesConvergeIndependentlyOfPosition()
    {
        var source = new SyncEngine(Guid.NewGuid(), Guid.NewGuid());
        var firstLayer = source.AddLayer("Перший");
        var secondLayer = source.AddLayer("Другий");
        var shape = source.AddShape(ShapeKind.Line, new(1, 2), new(3, 4), 0, 1);
        var lowerPeer = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var higherPeer = Guid.Parse("22222222-2222-2222-2222-222222222222");
        Operation[] operations =
        [
            .. source.GetOperations(),
            new ObjectLayer(new(Guid.NewGuid(), lowerPeer, 1, 10), shape.ShapeId, firstLayer.LayerId),
            new ObjectLayer(new(Guid.NewGuid(), higherPeer, 1, 10), shape.ShapeId, secondLayer.LayerId),
            new ObjectMove(new(Guid.NewGuid(), lowerPeer, 2, 11), shape.ShapeId, new(20, 30))
        ];
        for (int seed = 0; seed < 40; seed++)
        {
            var document = new DocumentState();
            var random = new Random(seed);
            foreach (var operation in operations.OrderBy(_ => random.Next()))
            {
                document.Apply(operation);
            }
            var actual = Assert.Single(document.GetShapes());
            Assert.Equal(secondLayer.LayerId, actual.LayerId);
            Assert.Equal(higherPeer, actual.LayerChangedAt!.Value.PeerId);
            Assert.Equal(new StrokePoint(20, 30), actual.Offset);
        }
    }

    [Fact]
    public void LayerClearRemovesObservedMembersAndKeepsConcurrentArrivals()
    {
        var first = new SyncEngine(Guid.NewGuid(), Guid.NewGuid());
        var layer = first.AddLayer("Активний");
        var known = first.AddShape(ShapeKind.Rectangle, new(1, 2), new(3, 4), 0, 1, layerId: layer.LayerId);
        var outside = first.AddShape(ShapeKind.Ellipse, new(5, 6), new(7, 8), 0, 1);
        var second = new SyncEngine(Guid.NewGuid(), first.SessionId);
        second.MergeSnapshot(first.CreateSnapshot());
        var clear = first.ClearLayer(layer.LayerId);
        Assert.Equal(known.ShapeId, Assert.Single(clear.StrokeIds));
        var concurrent = second.AddShape(ShapeKind.Line, new(9, 10), new(11, 12), 0, 1, layerId: layer.LayerId);
        var membership = second.MoveObjectToLayer(outside.ShapeId, layer.LayerId);
        Operation[] operations = [.. first.GetOperations(), concurrent, membership];
        for (int seed = 0; seed < 40; seed++)
        {
            var document = new DocumentState();
            var random = new Random(seed);
            foreach (var operation in operations.OrderBy(_ => random.Next()))
            {
                document.Apply(operation);
            }
            Assert.Equal(2, document.GetShapes().Length);
            Assert.All(document.GetShapes(), shape => Assert.Equal(layer.LayerId, shape.LayerId));
            Assert.Equal(known.ShapeId, Assert.Single(document.GetTombstones()));
        }
    }

    [Fact]
    public void GlobalClearAlsoTombstonesLatentObjectsOfDeletedLayer()
    {
        var engine = new SyncEngine(Guid.NewGuid(), Guid.NewGuid());
        var layer = engine.AddLayer("Шар");
        var shape = engine.AddShape(ShapeKind.Line, new(1, 2), new(3, 4), 0, 1, layerId: layer.LayerId);
        engine.RemoveLayer(layer.LayerId);
        Assert.Empty(engine.GetShapes());
        var clear = engine.ClearCanvas();
        Assert.Equal(shape.ShapeId, Assert.Single(clear.StrokeIds));
        Assert.Throws<InvalidOperationException>(() => engine.MoveObjectToLayer(shape.ShapeId, Layer.DefaultLayerId));
        engine.Receive(new ObjectLayer(new(Guid.NewGuid(), Guid.NewGuid(), 1, 100), shape.ShapeId, Layer.DefaultLayerId));
        Assert.Empty(engine.GetShapes());
        Assert.Equal(shape.ShapeId, Assert.Single(engine.GetTombstones()));
    }

    [Fact]
    public void LayerSnapshotPreservesTombstonesAndRejectsForgedMetadata()
    {
        var source = new SyncEngine(Guid.NewGuid(), Guid.NewGuid());
        var layer = source.AddLayer("Шар");
        source.AddShape(ShapeKind.Rectangle, new(1, 2), new(3, 4), 0, 1, layerId: layer.LayerId);
        source.RenameLayer(layer.LayerId, "Перейменований");
        source.SetLayerPosition(layer.LayerId, -1);
        source.RemoveLayer(Layer.DefaultLayerId);
        var snapshot = JsonSerializer.Deserialize<Snapshot>(JsonSerializer.Serialize(source.CreateSnapshot()))!;
        var restored = new SyncEngine(Guid.NewGuid(), source.SessionId);
        restored.MergeSnapshot(snapshot);

        Assert.Equal(source.GetLayers(), restored.GetLayers());
        Assert.Equal(source.GetShapes(), restored.GetShapes());
        Assert.Equal(snapshot.LayerTombstones, restored.CreateSnapshot().LayerTombstones);
        long time = restored.LamportTime;
        Assert.Throws<ArgumentException>(() => restored.MergeSnapshot(snapshot with { Layers = [snapshot.Layers[0] with { Name = "Підміна" }] }));
        Assert.Throws<ArgumentException>(() => restored.MergeSnapshot(snapshot with { LayerTombstones = [] }));
        Assert.Throws<ArgumentException>(() => restored.MergeSnapshot(snapshot with { Layers = default }));
        Assert.Equal(time, restored.LamportTime);
        Assert.Equal(source.GetLayers(), restored.GetLayers());
    }

    [Fact]
    public void InvalidLayerChangesDoNotConsumeSequenceNumbers()
    {
        var engine = new SyncEngine(Guid.NewGuid(), Guid.NewGuid());
        Assert.Throws<ArgumentException>(() => engine.AddLayer(" "));
        Assert.Throws<ArgumentException>(() => engine.AddLayer(new string('x', 101)));
        Assert.Throws<ArgumentException>(() => engine.AddLayer("Шар", double.NaN));
        Assert.Throws<InvalidOperationException>(() => engine.RenameLayer(Guid.NewGuid(), "Шар"));
        Assert.Throws<ArgumentException>(() => engine.RemoveLayer(Guid.Empty));
        var layer = engine.AddLayer(" Шар ");
        Assert.Equal(1, layer.Stamp.SequenceNumber);
        Assert.Equal("Шар", layer.Name);
        Assert.Throws<ArgumentException>(() => engine.RenameLayer(layer.LayerId, " "));
        Assert.Throws<ArgumentException>(() => engine.SetLayerPosition(layer.LayerId, double.PositiveInfinity));
        Assert.Throws<InvalidOperationException>(() => engine.MoveObjectToLayer(Guid.NewGuid(), layer.LayerId));
        Assert.Equal(2, engine.SetLayerPosition(layer.LayerId, -1).Stamp.SequenceNumber);
    }
}
