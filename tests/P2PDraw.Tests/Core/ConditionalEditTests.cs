using P2PDraw.Core.Models;
using P2PDraw.Core.Operations;
using P2PDraw.Core.Sync;
using Xunit;

namespace P2PDraw.Tests.Core;

public class ConditionalEditTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GuardedMoveAppliesToUnchangedShapeOrStroke(bool useStroke)
    {
        var engine = new SyncEngine(Guid.NewGuid(), Guid.NewGuid());
        Guid objectId = useStroke ? engine.BeginStroke(0, 1).StrokeId
            : engine.AddShape(ShapeKind.Rectangle, new(1, 2), new(3, 4), 0, 1).ShapeId;
        var first = engine.MoveObject(objectId, new(10, 20));

        var inverse = engine.TryMoveObject(objectId, first.Offset, first.Stamp, default);

        Assert.NotNull(inverse);
        Assert.Equal(default, inverse.Offset);
        Assert.Equal(3, inverse.Stamp.SequenceNumber);
        Assert.Equal(3, engine.GetOperations().Length);
        Assert.Null(engine.TryMoveObject(objectId, first.Offset, first.Stamp, new(30, 40)));
        Assert.Equal(3, engine.LamportTime);
        Assert.Equal(3, engine.GetOperations().Length);
    }

    [Fact]
    public void SameOffsetWithRemoteRevisionSkipsMoveWithoutConsumingStamp()
    {
        var engine = new SyncEngine(Guid.NewGuid(), Guid.NewGuid());
        var add = engine.AddShape(ShapeKind.Line, new(1, 2), new(3, 4), 0, 1);
        var local = engine.MoveObject(add.ShapeId, new(10, 20));
        var remote = new ObjectMove(new(Guid.NewGuid(), Guid.NewGuid(), 1, 100), add.ShapeId, local.Offset);
        engine.Receive(remote);
        long clock = engine.LamportTime;

        var skipped = engine.TryMoveObject(add.ShapeId, local.Offset, local.Stamp, default);

        Assert.Null(skipped);
        Assert.Equal(clock, engine.LamportTime);
        Assert.Equal(3, engine.GetOperations().Length);
        Assert.Equal(remote.Stamp, Assert.Single(engine.GetShapes()).MovedAt);
        Assert.Equal(3, engine.MoveObject(add.ShapeId, new(30, 40)).Stamp.SequenceNumber);
    }

    [Fact]
    public void GuardedResizePreservesIndependentMoveAndLayerChanges()
    {
        var engine = new SyncEngine(Guid.NewGuid(), Guid.NewGuid());
        var add = engine.AddShape(ShapeKind.Ellipse, new(1, 2), new(3, 4), 0, 1);
        engine.ResizeShape(add.ShapeId, new(5, 6), new(7, 8));
        var expected = Assert.Single(engine.GetShapes());
        var layer = engine.AddLayer("Інший");
        var remotePeer = Guid.NewGuid();
        var move = new ObjectMove(new(Guid.NewGuid(), remotePeer, 1, 100), add.ShapeId, new(10, 20));
        var membership = new ObjectLayer(new(Guid.NewGuid(), remotePeer, 2, 101), add.ShapeId, layer.LayerId);
        engine.Receive(move);
        engine.Receive(membership);

        var inverse = engine.TryResizeShape(expected, add.Start, add.End);

        Assert.NotNull(inverse);
        var shape = Assert.Single(engine.GetShapes());
        Assert.Equal(add.Start, shape.Start);
        Assert.Equal(add.End, shape.End);
        Assert.Equal(inverse.Stamp, shape.ResizedAt);
        Assert.Equal(move.Stamp, shape.MovedAt);
        Assert.Equal(move.Offset, shape.Offset);
        Assert.Equal(membership.Stamp, shape.LayerChangedAt);
        Assert.Equal(layer.LayerId, shape.LayerId);
    }

    [Fact]
    public void SameGeometryWithRemoteRevisionSkipsResizeWithoutConsumingStamp()
    {
        var engine = new SyncEngine(Guid.NewGuid(), Guid.NewGuid());
        var add = engine.AddShape(ShapeKind.Rectangle, new(1, 2), new(3, 4), 0, 1);
        engine.ResizeShape(add.ShapeId, new(5, 6), new(7, 8));
        var expected = Assert.Single(engine.GetShapes());
        var remote = new ShapeResize(new(Guid.NewGuid(), Guid.NewGuid(), 1, 100), add.ShapeId, expected.Start, expected.End);
        engine.Receive(remote);
        long clock = engine.LamportTime;

        var skipped = engine.TryResizeShape(expected, add.Start, add.End);

        Assert.Null(skipped);
        Assert.Equal(clock, engine.LamportTime);
        Assert.Equal(3, engine.GetOperations().Length);
        Assert.Equal(remote.Stamp, Assert.Single(engine.GetShapes()).ResizedAt);
        Assert.Equal(3, engine.ResizeShape(add.ShapeId, add.Start, add.End).Stamp.SequenceNumber);
    }

    [Fact]
    public void GuardedCreationRemovalChecksEveryIndependentRevision()
    {
        var engine = new SyncEngine(Guid.NewGuid(), Guid.NewGuid());
        var add = engine.AddShape(ShapeKind.Rectangle, new(1, 2), new(3, 4), 0, 1);
        var move = engine.MoveObject(add.ShapeId, new(10, 20));
        var resize = engine.ResizeShape(add.ShapeId, new(5, 6), new(7, 8));
        var layer = engine.AddLayer("Інший");
        var membership = engine.MoveObjectToLayer(add.ShapeId, layer.LayerId);
        long clock = engine.LamportTime;

        Assert.Null(engine.TryRemoveObject(add.ShapeId, null, resize.Stamp, membership.Stamp));
        Assert.Null(engine.TryRemoveObject(add.ShapeId, move.Stamp, null, membership.Stamp));
        Assert.Null(engine.TryRemoveObject(add.ShapeId, move.Stamp, resize.Stamp, null));
        Assert.Equal(clock, engine.LamportTime);
        Assert.Equal(5, engine.GetOperations().Length);
        Assert.Empty(engine.GetTombstones());
        var removed = engine.TryRemoveObject(add.ShapeId, move.Stamp, resize.Stamp, membership.Stamp);

        Assert.NotNull(removed);
        Assert.Equal(6, removed.Stamp.SequenceNumber);
        Assert.Empty(engine.GetShapes());
        Assert.Equal(add.ShapeId, Assert.Single(engine.GetTombstones()));
        Assert.Null(engine.TryRemoveObject(add.ShapeId, move.Stamp, resize.Stamp, membership.Stamp));
        Assert.Equal(6, engine.LamportTime);
    }

    [Fact]
    public void GuardedStrokeRemovalWaitsForCompletion()
    {
        var engine = new SyncEngine(Guid.NewGuid(), Guid.NewGuid());
        var begin = engine.BeginStroke(0, 1);
        engine.AppendStroke(begin.StrokeId, [new(1, 2)]);

        Assert.Null(engine.TryRemoveObject(begin.StrokeId, null, null, null));
        Assert.Equal(2, engine.LamportTime);
        engine.EndStroke(begin.StrokeId);
        Assert.Null(engine.TryRemoveObject(begin.StrokeId, null, begin.Stamp, null));
        var removed = engine.TryRemoveObject(begin.StrokeId, null, null, null);

        Assert.NotNull(removed);
        Assert.Equal(4, removed.Stamp.SequenceNumber);
        Assert.Empty(engine.GetStrokes());
    }

    [Fact]
    public void MissingOrDeletedLayerSkipsEveryGuardWithoutConsumingStamp()
    {
        var engine = new SyncEngine(Guid.NewGuid(), Guid.NewGuid());
        var add = engine.AddShape(ShapeKind.Line, new(1, 2), new(3, 4), 0, 1);
        var expected = Assert.Single(engine.GetShapes());
        engine.RemoveLayer(Layer.DefaultLayerId);
        long clock = engine.LamportTime;
        Guid missingId = Guid.NewGuid();

        foreach (var objectId in new[] { add.ShapeId, missingId })
        {
            Assert.Null(engine.TryMoveObject(objectId, default, null, new(10, 20)));
            Assert.Null(engine.TryResizeShape(expected with { ShapeId = objectId }, new(5, 6), new(7, 8)));
            Assert.Null(engine.TryRemoveObject(objectId, null, null, null));
        }

        Assert.Equal(clock, engine.LamportTime);
        Assert.Equal(2, engine.GetOperations().Length);
        Assert.Empty(engine.GetTombstones());
        Assert.Equal(3, engine.AddLayer("Новий").Stamp.SequenceNumber);
    }

    [Fact]
    public void MatchingGuardStillValidatesTargetGeometryBeforeCreatingOperation()
    {
        var engine = new SyncEngine(Guid.NewGuid(), Guid.NewGuid());
        var add = engine.AddShape(ShapeKind.Line, new(1, 2), new(3, 4), 0, 1);
        var expected = Assert.Single(engine.GetShapes());

        Assert.Throws<ArgumentException>(() => engine.TryMoveObject(add.ShapeId, default, null, new(double.NaN, 0)));
        Assert.Throws<ArgumentException>(() => engine.TryResizeShape(expected, new(1, 2), new(double.PositiveInfinity, 4)));
        Assert.Equal(1, engine.LamportTime);
        Assert.Single(engine.GetOperations());
        Assert.Equal(2, engine.TryMoveObject(add.ShapeId, default, null, new(10, 20))!.Stamp.SequenceNumber);
    }

    [Fact]
    public async Task ConcurrentGuardedMovesApplyAtMostOneCompensation()
    {
        var engine = new SyncEngine(Guid.NewGuid(), Guid.NewGuid());
        var add = engine.AddShape(ShapeKind.Line, new(1, 2), new(3, 4), 0, 1);
        using var start = new ManualResetEventSlim();
        var cancellationToken = TestContext.Current.CancellationToken;
        var first = Task.Run(() =>
        {
            start.Wait(cancellationToken);
            return engine.TryMoveObject(add.ShapeId, default, null, new(10, 20));
        }, cancellationToken);
        var second = Task.Run(() =>
        {
            start.Wait(cancellationToken);
            return engine.TryMoveObject(add.ShapeId, default, null, new(30, 40));
        }, cancellationToken);

        start.Set();
        var results = await Task.WhenAll(first, second);

        Assert.Single(results, operation => operation is not null);
        Assert.Equal(2, engine.GetOperations().Length);
        Assert.Equal(2, engine.LamportTime);
    }

    [Fact]
    public async Task ConcurrentReceiveCannotBeOverwrittenByStaleCompensation()
    {
        for (int attempt = 0; attempt < 40; attempt++)
        {
            var engine = new SyncEngine(Guid.NewGuid(), Guid.NewGuid());
            var add = engine.AddShape(ShapeKind.Line, new(1, 2), new(3, 4), 0, 1);
            var local = engine.MoveObject(add.ShapeId, new(10, 20));
            var remote = new ObjectMove(new(Guid.NewGuid(), Guid.NewGuid(), 1, 100), add.ShapeId, new(30, 40));
            using var start = new ManualResetEventSlim();
            var cancellationToken = TestContext.Current.CancellationToken;
            var receive = Task.Run(() =>
            {
                start.Wait(cancellationToken);
                engine.Receive(remote);
            }, cancellationToken);
            var undo = Task.Run(() =>
            {
                start.Wait(cancellationToken);
                return engine.TryMoveObject(add.ShapeId, local.Offset, local.Stamp, default);
            }, cancellationToken);

            start.Set();
            await Task.WhenAll(receive, undo);
            var compensation = await undo;

            var shape = Assert.Single(engine.GetShapes());
            Assert.Equal(remote.Offset, shape.Offset);
            Assert.Equal(remote.Stamp, shape.MovedAt);
            if (compensation is not null)
            {
                Assert.True(compensation.Stamp.LamportTime < remote.Stamp.LamportTime);
            }
        }
    }
}
