using System.Text.Json;
using P2PDraw.Core.Models;
using P2PDraw.Core.Operations;
using P2PDraw.Core.Sync;
using Xunit;

namespace P2PDraw.Tests.Core;

public class ShapeResizeTests
{
    [Theory]
    [InlineData(ShapeKind.Line)]
    [InlineData(ShapeKind.Rectangle)]
    [InlineData(ShapeKind.Ellipse)]
    public void ResizeChangesGeometryAndPreservesStyleOffsetAndLayer(ShapeKind kind)
    {
        var engine = new SyncEngine(Guid.NewGuid(), Guid.NewGuid());
        var layer = engine.AddLayer("Фігури");
        var add = engine.AddShape(kind, new(1, 2), new(3, 4), 0xFF123456, 3, 0xAA456789, layer.LayerId);
        var move = engine.MoveObject(add.ShapeId, new(10, 20));

        var resize = engine.ResizeShape(add.ShapeId, new(50, 60), new(-10, 20));

        var shape = Assert.Single(engine.GetShapes());
        Assert.Equal(resize.Start, shape.Start);
        Assert.Equal(resize.End, shape.End);
        Assert.Equal(resize.Stamp, shape.ResizedAt);
        Assert.Equal(add.ShapeId, shape.ShapeId);
        Assert.Equal(add.Stamp, shape.CreatedAt);
        Assert.Equal(kind, shape.Kind);
        Assert.Equal(add.Color, shape.Color);
        Assert.Equal(add.Width, shape.Width);
        Assert.Equal(add.Fill, shape.Fill);
        Assert.Equal(move.Offset, shape.Offset);
        Assert.Equal(move.Stamp, shape.MovedAt);
        Assert.Equal(layer.LayerId, shape.LayerId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConcurrentResizeUsesFullStampOrder(bool compareOperationIds)
    {
        var source = new SyncEngine(Guid.NewGuid(), Guid.NewGuid());
        var add = source.AddShape(ShapeKind.Rectangle, new(1, 2), new(3, 4), 0, 1);
        var lowerId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var higherId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        Guid firstPeer = compareOperationIds ? source.PeerId : lowerId;
        Guid secondPeer = compareOperationIds ? source.PeerId : higherId;
        var first = new ShapeResize(new(lowerId, firstPeer, 2, 10), add.ShapeId, new(5, 6), new(7, 8));
        var winner = new ShapeResize(new(higherId, secondPeer, 3, 10), add.ShapeId, new(9, 10), new(11, 12));
        var move = new ObjectMove(new(Guid.NewGuid(), lowerId, 4, 20), add.ShapeId, new(30, 40));
        Operation[] operations = [add, first, winner, move];

        for (int seed = 0; seed < 40; seed++)
        {
            var document = new DocumentState();
            var random = new Random(seed);
            foreach (var operation in operations.OrderBy(_ => random.Next()))
            {
                Assert.True(document.Apply(operation));
                Assert.False(document.Apply(operation));
            }
            var shape = Assert.Single(document.GetShapes());
            Assert.Equal(winner.Start, shape.Start);
            Assert.Equal(winner.End, shape.End);
            Assert.Equal(winner.Stamp, shape.ResizedAt);
            Assert.Equal(move.Offset, shape.Offset);
            Assert.Equal(move.Stamp, shape.MovedAt);
            Assert.Empty(document.GetPendingStrokeIds());
            Assert.Equal(operations.Length, document.OperationCount);
        }
    }

    [Fact]
    public void ResizeBeforeCreationSurvivesSnapshotAndWaitsForMissingOperation()
    {
        var source = new SyncEngine(Guid.NewGuid(), Guid.NewGuid());
        var add = source.AddShape(ShapeKind.Ellipse, new(1, 2), new(3, 4), 0, 1);
        var resize = source.ResizeShape(add.ShapeId, new(5, 6), new(7, 8));
        var receiving = new SyncEngine(Guid.NewGuid(), source.SessionId);
        receiving.Receive(resize);
        var snapshot = JsonSerializer.Deserialize<Snapshot>(JsonSerializer.Serialize(receiving.CreateSnapshot()))!;
        var restored = new SyncEngine(Guid.NewGuid(), source.SessionId);
        restored.MergeSnapshot(snapshot);
        Assert.Empty(restored.GetShapes());
        Assert.Equal(add.ShapeId, Assert.Single(restored.GetPendingStrokeIds()));

        restored.Receive(add);

        var shape = Assert.Single(restored.GetShapes());
        Assert.Equal(resize.Start, shape.Start);
        Assert.Equal(resize.End, shape.End);
        Assert.Equal(resize.Stamp, shape.ResizedAt);
        Assert.Empty(restored.GetPendingStrokeIds());
        Assert.False(restored.Receive(resize));
        Assert.Equal(2, restored.GetOperations().Length);
    }

    [Fact]
    public void GeometryOlderThanCreationDoesNotReplaceInitialGeometry()
    {
        var peer = Guid.NewGuid();
        var shapeId = Guid.NewGuid();
        var add = new ShapeAdd(new(Guid.NewGuid(), peer, 2, 10), shapeId, ShapeKind.Line, new(1, 2), new(3, 4), 0, 1);
        var resize = new ShapeResize(new(Guid.NewGuid(), peer, 1, 5), shapeId, new(5, 6), new(7, 8));
        Operation[][] orders = [[add, resize], [resize, add]];
        foreach (var operations in orders)
        {
            var document = new DocumentState();
            foreach (var operation in operations)
            {
                document.Apply(operation);
            }
            var shape = Assert.Single(document.GetShapes());
            Assert.Equal(add.Start, shape.Start);
            Assert.Equal(add.End, shape.End);
            Assert.Null(shape.ResizedAt);
            Assert.Empty(document.GetPendingStrokeIds());
        }
    }

    [Fact]
    public void ShapeResizeCannotChangeStrokeWithCollidingId()
    {
        var source = new SyncEngine(Guid.NewGuid(), Guid.NewGuid());
        var stroke = source.BeginStroke(0, 1);
        source.AppendStroke(stroke.StrokeId, [new(1, 2), new(3, 4)]);
        source.EndStroke(stroke.StrokeId);
        var resize = new ShapeResize(new(Guid.NewGuid(), Guid.NewGuid(), 1, 10), stroke.StrokeId, new(5, 6), new(7, 8));
        var receiver = new SyncEngine(Guid.NewGuid(), source.SessionId);
        receiver.Receive(resize);

        receiver.MergeSnapshot(source.CreateSnapshot());

        Assert.Empty(receiver.GetShapes());
        Assert.Empty(receiver.GetPendingStrokeIds());
        var actual = Assert.Single(receiver.GetStrokes());
        Assert.Equal(source.GetStrokes()[0].Points, actual.Points);
        Assert.Equal(source.GetStrokes()[0].CreatedAt, actual.CreatedAt);
        Assert.True(actual.IsComplete);
        Assert.Throws<InvalidOperationException>(() => receiver.ResizeShape(stroke.StrokeId, new(5, 6), new(7, 8)));
    }

    [Fact]
    public void ObjectTombstoneWinsOverLateResizeInEveryDeliveryOrder()
    {
        var source = new SyncEngine(Guid.NewGuid(), Guid.NewGuid());
        var add = source.AddShape(ShapeKind.Rectangle, new(1, 2), new(3, 4), 0, 1);
        var remove = source.RemoveObject(add.ShapeId);
        var late = new ShapeResize(new(Guid.NewGuid(), Guid.NewGuid(), 1, 100), add.ShapeId, new(5, 6), new(7, 8));
        Operation[] operations = [add, remove, late];
        for (int seed = 0; seed < 30; seed++)
        {
            var document = new DocumentState();
            var random = new Random(seed);
            foreach (var operation in operations.OrderBy(_ => random.Next()))
            {
                document.Apply(operation);
            }
            Assert.Empty(document.GetShapes());
            Assert.Empty(document.GetPendingStrokeIds());
            Assert.Equal(add.ShapeId, Assert.Single(document.GetTombstones()));
        }
    }

    [Fact]
    public void ResizeDoesNotMakeObjectInDeletedLayerVisible()
    {
        var source = new SyncEngine(Guid.NewGuid(), Guid.NewGuid());
        var layer = source.AddLayer("Шар");
        var add = source.AddShape(ShapeKind.Ellipse, new(1, 2), new(3, 4), 0, 1, layerId: layer.LayerId);
        var remove = source.RemoveLayer(layer.LayerId);
        var late = new ShapeResize(new(Guid.NewGuid(), Guid.NewGuid(), 1, 100), add.ShapeId, new(5, 6), new(7, 8));
        Operation[] operations = [layer, add, remove, late];
        for (int seed = 0; seed < 30; seed++)
        {
            var receiver = new SyncEngine(Guid.NewGuid(), source.SessionId);
            var random = new Random(seed);
            foreach (var operation in operations.OrderBy(_ => random.Next()))
            {
                receiver.Receive(operation);
            }
            Assert.Empty(receiver.GetShapes());
            Assert.Empty(receiver.GetTombstones());
            Assert.Throws<InvalidOperationException>(() => receiver.ResizeShape(add.ShapeId, new(9, 10), new(11, 12)));

            receiver.MoveObjectToLayer(add.ShapeId, Layer.DefaultLayerId);

            var shape = Assert.Single(receiver.GetShapes());
            Assert.Equal(late.Start, shape.Start);
            Assert.Equal(late.End, shape.End);
            Assert.Equal(late.Stamp, shape.ResizedAt);
        }
    }

    [Fact]
    public void SnapshotRestoresGeometryAndRejectsForgedResizeStamp()
    {
        var source = new SyncEngine(Guid.NewGuid(), Guid.NewGuid());
        var add = source.AddShape(ShapeKind.Rectangle, new(1, 2), new(3, 4), 0, 1);
        source.ResizeShape(add.ShapeId, new(5, 6), new(7, 8));
        source.MoveObject(add.ShapeId, new(10, 20));
        var snapshot = JsonSerializer.Deserialize<Snapshot>(JsonSerializer.Serialize(source.CreateSnapshot()))!;
        var restored = new SyncEngine(Guid.NewGuid(), source.SessionId);

        restored.MergeSnapshot(snapshot);

        Assert.Equal(source.GetShapes(), restored.GetShapes());
        long logicalTime = restored.LamportTime;
        var forged = snapshot with { Shapes = [snapshot.Shapes[0] with { ResizedAt = add.Stamp }] };
        Assert.Throws<ArgumentException>(() => restored.MergeSnapshot(forged));
        Assert.Equal(logicalTime, restored.LamportTime);
        Assert.Equal(source.GetShapes(), restored.GetShapes());
    }

    [Fact]
    public void InvalidResizeDoesNotConsumeLogicalTimeOrSequence()
    {
        var engine = new SyncEngine(Guid.NewGuid(), Guid.NewGuid());
        var add = engine.AddShape(ShapeKind.Line, new(1, 2), new(3, 4), 0, 1);
        Assert.Throws<ArgumentException>(() => engine.ResizeShape(Guid.Empty, new(1, 2), new(3, 4)));
        Assert.Throws<ArgumentException>(() => engine.ResizeShape(add.ShapeId, new(double.NaN, 2), new(3, 4)));
        Assert.Throws<ArgumentException>(() => engine.ResizeShape(add.ShapeId, new(1, 2), new(3, double.PositiveInfinity)));
        Assert.Throws<InvalidOperationException>(() => engine.ResizeShape(Guid.NewGuid(), new(1, 2), new(3, 4)));
        var invalidRemote = new ShapeResize(new(Guid.NewGuid(), Guid.NewGuid(), 1, 100), add.ShapeId, new(1, 2), new(double.NaN, 4));
        Assert.Throws<ArgumentException>(() => engine.Receive(invalidRemote));
        Assert.Equal(1, engine.LamportTime);
        Assert.Single(engine.GetOperations());

        var resize = engine.ResizeShape(add.ShapeId, new(5, 6), new(7, 8));

        Assert.Equal(2, resize.Stamp.SequenceNumber);
        Assert.Equal(2, resize.Stamp.LamportTime);
        engine.RemoveObject(add.ShapeId);
        Assert.Throws<InvalidOperationException>(() => engine.ResizeShape(add.ShapeId, new(9, 10), new(11, 12)));
        Assert.Equal(4, engine.ClearCanvas().Stamp.SequenceNumber);
    }
}
