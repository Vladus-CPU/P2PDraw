using System.Text.Json;
using P2PDraw.Core.Models;
using P2PDraw.Core.Operations;
using P2PDraw.Core.Sync;
using Xunit;

namespace P2PDraw.Tests.Core;

public class ShapeTests
{
    [Theory]
    [InlineData(ShapeKind.Line)]
    [InlineData(ShapeKind.Rectangle)]
    [InlineData(ShapeKind.Ellipse)]
    public void ShapesKeepVectorCoordinatesAndStyle(ShapeKind kind)
    {
        var engine = new SyncEngine(Guid.NewGuid(), Guid.NewGuid());
        var operation = engine.AddShape(kind, new(50, 60), new(-10, 20), 0xFF123456, 4, 0xAA456789);

        var shape = Assert.Single(engine.GetShapes());
        Assert.Equal(operation.ShapeId, shape.ShapeId);
        Assert.Equal(engine.PeerId, shape.PeerId);
        Assert.Equal(kind, shape.Kind);
        Assert.Equal(new StrokePoint(50, 60), shape.Start);
        Assert.Equal(new StrokePoint(-10, 20), shape.End);
        Assert.Equal(0xFF123456u, shape.Color);
        Assert.Equal(0xAA456789u, shape.Fill);
        Assert.Equal(4, shape.Width);
        Assert.Equal(operation.Stamp, shape.CreatedAt);
        Assert.Equal(1, operation.Stamp.SequenceNumber);
        Assert.Equal(1, engine.LamportTime);
    }

    [Fact]
    public void ConcurrentShapesSurviveReorderedDuplicateDelivery()
    {
        var first = new SyncEngine(Guid.NewGuid(), Guid.NewGuid());
        var second = new SyncEngine(Guid.NewGuid(), first.SessionId);
        Operation[] operations =
        [
            first.AddShape(ShapeKind.Line, new(1, 2), new(3, 4), 0, 1),
            second.AddShape(ShapeKind.Rectangle, new(5, 6), new(7, 8), 1, 2),
            first.AddShape(ShapeKind.Ellipse, new(9, 10), new(11, 12), 2, 3)
        ];
        var expected = new DocumentState();
        foreach (var operation in operations)
        {
            expected.Apply(operation);
        }
        for (int seed = 0; seed < 30; seed++)
        {
            var actual = new DocumentState();
            var random = new Random(seed);
            foreach (var operation in operations.OrderBy(_ => random.Next()))
            {
                Assert.True(actual.Apply(operation));
                Assert.False(actual.Apply(operation));
            }
            Assert.Equal(expected.GetShapes(), actual.GetShapes());
            Assert.Empty(actual.GetStrokes());
            Assert.Empty(actual.GetPendingStrokeIds());
            Assert.Equal(3, actual.OperationCount);
        }
    }

    [Fact]
    public void ClearRemovesKnownShapesAndStrokesButKeepsConcurrentShape()
    {
        var first = new SyncEngine(Guid.NewGuid(), Guid.NewGuid());
        var second = new SyncEngine(Guid.NewGuid(), first.SessionId);
        var stroke = first.BeginStroke(0, 1);
        var knownShape = first.AddShape(ShapeKind.Rectangle, new(1, 2), new(3, 4), 0, 1);
        var clear = first.ClearCanvas();
        var concurrent = second.AddShape(ShapeKind.Ellipse, new(5, 6), new(7, 8), 0, 1);
        Operation[] operations = [stroke, knownShape, clear, concurrent];
        Assert.Contains(stroke.StrokeId, clear.StrokeIds);
        Assert.Contains(knownShape.ShapeId, clear.StrokeIds);

        for (int seed = 0; seed < 30; seed++)
        {
            var document = new DocumentState();
            var random = new Random(seed);
            foreach (var operation in operations.OrderBy(_ => random.Next()))
            {
                document.Apply(operation);
            }
            Assert.Equal(concurrent.ShapeId, Assert.Single(document.GetShapes()).ShapeId);
            Assert.Empty(document.GetStrokes());
            Assert.Equal(2, document.GetTombstones().Length);
            Assert.Empty(document.GetPendingStrokeIds());
        }
    }

    [Fact]
    public void SnapshotRestoresShapesAndRejectsTamperedGeometry()
    {
        var source = new SyncEngine(Guid.NewGuid(), Guid.NewGuid());
        source.AddShape(ShapeKind.Line, new(1, 2), new(3, 4), 0, 1);
        source.ClearCanvas();
        source.BeginStroke(0, 2);
        source.AddShape(ShapeKind.Ellipse, new(5, 6), new(7, 8), 1, 3, 2);
        var snapshot = JsonSerializer.Deserialize<Snapshot>(JsonSerializer.Serialize(source.CreateSnapshot()))!;
        var restored = new SyncEngine(Guid.NewGuid(), source.SessionId);

        restored.MergeSnapshot(snapshot);

        Assert.Equal(source.GetShapes(), restored.GetShapes());
        Assert.Equal(source.GetTombstones(), restored.GetTombstones());
        Assert.Equal(source.GetStrokes()[0].StrokeId, Assert.Single(restored.GetStrokes()).StrokeId);
        Assert.Equal(source.GetOperations().Select(operation => operation.Stamp.OperationId),
            restored.GetOperations().Select(operation => operation.Stamp.OperationId));
        var tampered = snapshot with { Shapes = [snapshot.Shapes[0] with { End = new(99, 100) }] };
        Assert.Throws<ArgumentException>(() => restored.MergeSnapshot(tampered));
        Assert.Throws<ArgumentException>(() => restored.MergeSnapshot(snapshot with { Shapes = default }));
        Assert.Equal(source.GetShapes(), restored.GetShapes());
    }

    [Fact]
    public void StrokeAndShapeIdCollisionUsesStableCreationOrder()
    {
        var peer = Guid.NewGuid();
        var objectId = Guid.NewGuid();
        var stroke = new StrokeBegin(new(Guid.NewGuid(), peer, 1, 1), objectId, 0, 1);
        var shape = new ShapeAdd(new(Guid.NewGuid(), peer, 2, 2), objectId, ShapeKind.Rectangle, new(1, 2), new(3, 4), 0, 1);
        Operation[][] orders = [[stroke, shape], [shape, stroke]];
        foreach (var order in orders)
        {
            var document = new DocumentState();
            foreach (var operation in order)
            {
                document.Apply(operation);
            }
            Assert.Equal(objectId, Assert.Single(document.GetStrokes()).StrokeId);
            Assert.Empty(document.GetShapes());
            Assert.Equal(2, document.OperationCount);
        }
    }

    [Fact]
    public void InvalidShapeDoesNotConsumeLogicalTimeOrSequence()
    {
        var engine = new SyncEngine(Guid.NewGuid(), Guid.NewGuid());
        Assert.Throws<ArgumentException>(() => engine.AddShape((ShapeKind)99, new(1, 2), new(3, 4), 0, 1));
        Assert.Throws<ArgumentException>(() => engine.AddShape(ShapeKind.Line, new(double.NaN, 2), new(3, 4), 0, 1));
        Assert.Throws<ArgumentException>(() => engine.AddShape(ShapeKind.Ellipse, new(1, 2), new(3, double.PositiveInfinity), 0, 1));
        Assert.Throws<ArgumentException>(() => engine.AddShape(ShapeKind.Rectangle, new(1, 2), new(3, 4), 0, 0));
        var operation = engine.AddShape(ShapeKind.Line, new(1, 2), new(3, 4), 0, 1);
        Assert.Equal(1, operation.Stamp.SequenceNumber);
        Assert.Equal(1, operation.Stamp.LamportTime);
    }
}
