using System.Text.Json;
using P2PDraw.Core.Models;
using P2PDraw.Core.Operations;
using P2PDraw.Core.Sync;
using Xunit;

namespace P2PDraw.Tests.Core;

public class ObjectMoveTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConcurrentMovesUseStableStampOrderForShapesAndStrokes(bool useStroke)
    {
        var source = new SyncEngine(Guid.NewGuid(), Guid.NewGuid());
        Guid objectId;
        if (useStroke)
        {
            var begin = source.BeginStroke(0, 2);
            source.AppendStroke(begin.StrokeId, [new(1, 2), new(3, 4)]);
            source.EndStroke(begin.StrokeId);
            objectId = begin.StrokeId;
        }
        else
        {
            objectId = source.AddShape(ShapeKind.Rectangle, new(1, 2), new(3, 4), 0, 2).ShapeId;
        }
        var lowerPeer = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var higherPeer = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var firstMove = new ObjectMove(new(Guid.NewGuid(), lowerPeer, 1, 10), objectId, new(10, 20));
        var winningMove = new ObjectMove(new(Guid.NewGuid(), higherPeer, 1, 10), objectId, new(30, 40));
        Operation[] operations = [.. source.GetOperations(), firstMove, winningMove];

        for (int seed = 0; seed < 40; seed++)
        {
            var document = new DocumentState();
            var random = new Random(seed);
            foreach (var operation in operations.OrderBy(_ => random.Next()))
            {
                document.Apply(operation);
                Assert.False(document.Apply(operation));
            }
            Assert.Empty(document.GetPendingStrokeIds());
            Assert.Equal(operations.Length, document.OperationCount);
            if (useStroke)
            {
                var stroke = Assert.Single(document.GetStrokes());
                Assert.Equal(winningMove.Offset, stroke.Offset);
                Assert.Equal(winningMove.Stamp, stroke.MovedAt);
                Assert.Equal(new StrokePoint[] { new(1, 2), new(3, 4) }, stroke.Points);
                Assert.True(stroke.IsComplete);
            }
            else
            {
                var shape = Assert.Single(document.GetShapes());
                Assert.Equal(winningMove.Offset, shape.Offset);
                Assert.Equal(winningMove.Stamp, shape.MovedAt);
                Assert.Equal(new StrokePoint(1, 2), shape.Start);
                Assert.Equal(new StrokePoint(3, 4), shape.End);
            }
        }
    }

    [Fact]
    public void EqualTimeAndPeerMovesUseOperationIdTieBreak()
    {
        var source = new SyncEngine(Guid.NewGuid(), Guid.NewGuid());
        var add = source.AddShape(ShapeKind.Line, new(1, 2), new(3, 4), 0, 1);
        var lowerId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var higherId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var first = new ObjectMove(new(lowerId, source.PeerId, 2, 2), add.ShapeId, new(10, 20));
        var second = new ObjectMove(new(higherId, source.PeerId, 3, 2), add.ShapeId, new(30, 40));
        Operation[][] orders = [[add, first, second], [second, first, add], [first, add, second]];
        foreach (var operations in orders)
        {
            var document = new DocumentState();
            foreach (var operation in operations)
            {
                document.Apply(operation);
            }
            Assert.Equal(second.Offset, Assert.Single(document.GetShapes()).Offset);
        }
    }

    [Fact]
    public void OrphanMoveSurvivesSnapshotAndWaitsForCreation()
    {
        var source = new SyncEngine(Guid.NewGuid(), Guid.NewGuid());
        var add = source.AddShape(ShapeKind.Ellipse, new(1, 2), new(3, 4), 0, 1);
        var move = source.MoveObject(add.ShapeId, new(10, 20));
        var receiving = new SyncEngine(Guid.NewGuid(), source.SessionId);
        receiving.Receive(move);
        var restored = new SyncEngine(Guid.NewGuid(), source.SessionId);
        restored.MergeSnapshot(receiving.CreateSnapshot());
        Assert.Equal(add.ShapeId, Assert.Single(restored.GetPendingStrokeIds()));
        Assert.Empty(restored.GetShapes());

        restored.Receive(add);

        Assert.Empty(restored.GetPendingStrokeIds());
        Assert.Equal(move.Offset, Assert.Single(restored.GetShapes()).Offset);
        Assert.Equal(move.Stamp, restored.GetShapes()[0].MovedAt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TombstoneWinsOverLateMovesAndRepeatedRemoval(bool useStroke)
    {
        var source = new SyncEngine(Guid.NewGuid(), Guid.NewGuid());
        Guid objectId = useStroke ? source.BeginStroke(0, 1).StrokeId
            : source.AddShape(ShapeKind.Line, new(1, 2), new(3, 4), 0, 1).ShapeId;
        source.MoveObject(objectId, new(10, 20));
        source.RemoveObject(objectId);
        source.RemoveObject(objectId);
        var late = new ObjectMove(new(Guid.NewGuid(), Guid.NewGuid(), 1, 100), objectId, new(50, 60));
        Operation[] operations = [.. source.GetOperations(), late];
        for (int seed = 0; seed < 40; seed++)
        {
            var document = new DocumentState();
            var random = new Random(seed);
            foreach (var operation in operations.OrderBy(_ => random.Next()))
            {
                document.Apply(operation);
            }
            Assert.Empty(document.GetShapes());
            Assert.Empty(document.GetStrokes());
            Assert.Empty(document.GetPendingStrokeIds());
            Assert.Equal(objectId, Assert.Single(document.GetTombstones()));
            Assert.Equal(operations.Length, document.OperationCount);
        }
        Assert.Throws<InvalidOperationException>(() => source.MoveObject(objectId, new(1, 2)));
    }

    [Fact]
    public void SnapshotRestoresMovesAndRejectsForgedOffsets()
    {
        var source = new SyncEngine(Guid.NewGuid(), Guid.NewGuid());
        var stroke = source.BeginStroke(0, 1);
        source.AppendStroke(stroke.StrokeId, [new(1, 2)]);
        source.EndStroke(stroke.StrokeId);
        var shape = source.AddShape(ShapeKind.Rectangle, new(3, 4), new(5, 6), 1, 2);
        source.MoveObject(stroke.StrokeId, new(10, 20));
        source.MoveObject(shape.ShapeId, new(30, 40));
        var removed = source.AddShape(ShapeKind.Line, new(7, 8), new(9, 10), 0, 1);
        source.RemoveObject(removed.ShapeId);
        var snapshot = JsonSerializer.Deserialize<Snapshot>(JsonSerializer.Serialize(source.CreateSnapshot()))!;
        var restored = new SyncEngine(Guid.NewGuid(), source.SessionId);

        restored.MergeSnapshot(snapshot);

        Assert.Equal(source.GetShapes(), restored.GetShapes());
        Assert.Equal(source.GetStrokes()[0].Offset, Assert.Single(restored.GetStrokes()).Offset);
        Assert.Equal(source.GetTombstones(), restored.GetTombstones());
        Assert.Equal(source.GetOperations().Select(operation => operation.Stamp.OperationId),
            restored.GetOperations().Select(operation => operation.Stamp.OperationId));
        Assert.True(restored.LamportTime > source.LamportTime);
        Assert.Throws<ArgumentException>(() => restored.MergeSnapshot(snapshot with
        {
            Shapes = [snapshot.Shapes[0] with { Offset = new(99, 100) }]
        }));
        Assert.Equal(source.GetShapes(), restored.GetShapes());
        Assert.False(restored.Receive(source.GetOperations().Last()));
    }

    [Fact]
    public void InvalidLocalMovesDoNotConsumeStamps()
    {
        var engine = new SyncEngine(Guid.NewGuid(), Guid.NewGuid());
        var add = engine.AddShape(ShapeKind.Line, new(1, 2), new(3, 4), 0, 1);
        Assert.Throws<ArgumentException>(() => engine.MoveObject(Guid.Empty, new(1, 2)));
        Assert.Throws<ArgumentException>(() => engine.MoveObject(add.ShapeId, new(double.NaN, 2)));
        Assert.Throws<ArgumentException>(() => engine.MoveObject(add.ShapeId, new(1, double.PositiveInfinity)));
        Assert.Throws<InvalidOperationException>(() => engine.MoveObject(Guid.NewGuid(), new(1, 2)));
        Assert.Throws<ArgumentException>(() => engine.RemoveObject(Guid.Empty));
        Assert.Equal(2, engine.MoveObject(add.ShapeId, new(1, 2)).Stamp.SequenceNumber);
        engine.RemoveObject(add.ShapeId);
        Assert.Throws<InvalidOperationException>(() => engine.MoveObject(add.ShapeId, new(3, 4)));
        Assert.Equal(4, engine.ClearCanvas().Stamp.SequenceNumber);
        Assert.Equal(4, engine.LamportTime);
    }
}
