using System.Text.Json;
using P2PDraw.App.Editing;
using P2PDraw.Core.Models;
using P2PDraw.Core.Operations;
using P2PDraw.Core.Sync;
using Xunit;

namespace P2PDraw.Tests.App;

public class AppHistoryTests
{
    [Fact]
    public void CreationRedoUsesFreshIdsAndKeepsTombstones()
    {
        var engine = new SyncEngine(Guid.NewGuid(), Guid.NewGuid());
        var history = new EditHistory(() => engine, _ => { });
        var added = engine.AddShape(ShapeKind.Rectangle, new(1, 2), new(30, 40), 0xFF112233, 4, 0xFF223344);
        history.RecordCreation(added.ShapeId);
        var ids = new HashSet<Guid> { added.ShapeId };

        for (int cycle = 0; cycle < 3; cycle++)
        {
            Assert.True(history.Undo());
            Assert.Empty(engine.GetShapes());
            Assert.Equal(ids.Count, engine.GetTombstones().Length);
            Assert.True(history.Redo());
            var recreated = Assert.Single(engine.GetShapes());
            Assert.True(ids.Add(recreated.ShapeId));
            Assert.Equal(added.Start, recreated.Start);
            Assert.Equal(added.End, recreated.End);
            Assert.Equal(added.Color, recreated.Color);
            Assert.Equal(added.Width, recreated.Width);
            Assert.Equal(added.Fill, recreated.Fill);
        }
        Assert.Equal(7, engine.GetOperations().Length);
        Assert.True(history.CanUndo);
        Assert.False(history.CanRedo);
    }

    [Fact]
    public void StrokeRedoPreservesVectorPointsStyleAndOffset()
    {
        var engine = new SyncEngine(Guid.NewGuid(), Guid.NewGuid());
        var history = new EditHistory(() => engine, _ => { });
        var added = engine.BeginStroke(0xFF123456, 5);
        engine.AppendStroke(added.StrokeId, [new(1, 2), new(3, 4)]);
        engine.EndStroke(added.StrokeId);
        engine.MoveObject(added.StrokeId, new(12, 14));
        history.RecordCreation(added.StrokeId);
        var original = Assert.Single(engine.GetStrokes());

        Assert.True(history.Undo());
        Assert.True(history.Redo());

        var recreated = Assert.Single(engine.GetStrokes());
        Assert.NotEqual(original.StrokeId, recreated.StrokeId);
        Assert.Equal(original.Points.ToArray(), recreated.Points.ToArray());
        Assert.Equal(original.Offset, recreated.Offset);
        Assert.Equal(original.Color, recreated.Color);
        Assert.Equal(original.Width, recreated.Width);
        Assert.Equal(original.LayerId, recreated.LayerId);
        Assert.True(recreated.IsComplete);
        Assert.Equal(9, engine.GetOperations().Length);
    }

    [Fact]
    public void MoveAndResizeChainsFollowRepeatedlyRecreatedObjectIds()
    {
        var engine = new SyncEngine(Guid.NewGuid(), Guid.NewGuid());
        var history = new EditHistory(() => engine, _ => { });
        var added = engine.AddShape(ShapeKind.Ellipse, new(1, 2), new(30, 40), 0, 2);
        history.RecordCreation(added.ShapeId);
        history.RecordMove(engine.MoveObject(added.ShapeId, new(10, 12)));
        history.RecordResize(engine.ResizeShape(added.ShapeId, new(4, 5), new(60, 70)));
        history.RecordMove(engine.MoveObject(added.ShapeId, new(20, 24)));
        var ids = new HashSet<Guid> { added.ShapeId };

        for (int cycle = 0; cycle < 2; cycle++)
        {
            for (int action = 0; action < 4; action++)
            {
                Assert.True(history.Undo());
            }
            Assert.Empty(engine.GetShapes());
            for (int action = 0; action < 4; action++)
            {
                Assert.True(history.Redo());
            }
            var recreated = Assert.Single(engine.GetShapes());
            Assert.True(ids.Add(recreated.ShapeId));
            Assert.Equal(new StrokePoint(20, 24), recreated.Offset);
            Assert.Equal(new StrokePoint(4, 5), recreated.Start);
            Assert.Equal(new StrokePoint(60, 70), recreated.End);
        }
        Guid currentId = engine.GetShapes()[0].ShapeId;
        history.RecordMove(engine.MoveObject(currentId, new(50, 60)));
        Assert.True(history.Undo());
        Assert.Equal(new StrokePoint(20, 24), engine.GetShapes()[0].Offset);
        Assert.True(history.Undo());
        Assert.Equal(new StrokePoint(10, 12), engine.GetShapes()[0].Offset);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ForeignMoveBlocksUndoEvenWhenOffsetIsUnchanged(bool sameValue)
    {
        var engine = new SyncEngine(Guid.NewGuid(), Guid.NewGuid());
        var history = new EditHistory(() => engine, _ => { });
        var added = engine.AddShape(ShapeKind.Line, new(1, 2), new(30, 40), 0, 2);
        history.RecordCreation(added.ShapeId);
        history.RecordMove(engine.MoveObject(added.ShapeId, new(10, 20)));
        var remote = new SyncEngine(Guid.NewGuid(), engine.SessionId);
        remote.MergeSnapshot(engine.CreateSnapshot());
        var target = sameValue ? new StrokePoint(10, 20) : new StrokePoint(40, 50);
        engine.Receive(remote.MoveObject(added.ShapeId, target));
        int count = engine.GetOperations().Length;

        Assert.False(history.Undo());
        Assert.Equal(count, engine.GetOperations().Length);
        Assert.Equal(target, engine.GetShapes()[0].Offset);
        Assert.False(history.Undo());
        Assert.Single(engine.GetShapes());
        Assert.False(history.CanUndo);
        Assert.False(history.CanRedo);
    }

    [Fact]
    public void ForeignResizeDoesNotBlockUndoOfIndependentMove()
    {
        var engine = new SyncEngine(Guid.NewGuid(), Guid.NewGuid());
        var history = new EditHistory(() => engine, _ => { });
        var added = engine.AddShape(ShapeKind.Rectangle, new(1, 2), new(30, 40), 0, 2);
        history.RecordCreation(added.ShapeId);
        history.RecordMove(engine.MoveObject(added.ShapeId, new(10, 20)));
        var remote = new SyncEngine(Guid.NewGuid(), engine.SessionId);
        remote.MergeSnapshot(engine.CreateSnapshot());
        engine.Receive(remote.ResizeShape(added.ShapeId, new(4, 5), new(60, 70)));

        Assert.True(history.Undo());

        var shape = Assert.Single(engine.GetShapes());
        Assert.Equal(default, shape.Offset);
        Assert.Equal(new StrokePoint(4, 5), shape.Start);
        Assert.Equal(new StrokePoint(60, 70), shape.End);
        Assert.False(history.Undo());
        Assert.Single(engine.GetShapes());
    }

    [Fact]
    public void ForeignMoveDoesNotBlockUndoOfIndependentResize()
    {
        var engine = new SyncEngine(Guid.NewGuid(), Guid.NewGuid());
        var history = new EditHistory(() => engine, _ => { });
        var added = engine.AddShape(ShapeKind.Rectangle, new(1, 2), new(30, 40), 0, 2);
        history.RecordResize(engine.ResizeShape(added.ShapeId, new(4, 5), new(60, 70)));
        var remote = new SyncEngine(Guid.NewGuid(), engine.SessionId);
        remote.MergeSnapshot(engine.CreateSnapshot());
        engine.Receive(remote.MoveObject(added.ShapeId, new(10, 20)));

        Assert.True(history.Undo());

        var shape = Assert.Single(engine.GetShapes());
        Assert.Equal(new StrokePoint(10, 20), shape.Offset);
        Assert.Equal(added.Start, shape.Start);
        Assert.Equal(added.End, shape.End);
    }

    [Fact]
    public void ForeignMoveBetweenUndoAndRedoIsPreserved()
    {
        var engine = new SyncEngine(Guid.NewGuid(), Guid.NewGuid());
        var history = new EditHistory(() => engine, _ => { });
        var added = engine.AddShape(ShapeKind.Line, new(1, 2), new(30, 40), 0, 2);
        history.RecordMove(engine.MoveObject(added.ShapeId, new(10, 20)));
        Assert.True(history.Undo());
        var remote = new SyncEngine(Guid.NewGuid(), engine.SessionId);
        remote.MergeSnapshot(engine.CreateSnapshot());
        engine.Receive(remote.MoveObject(added.ShapeId, default));
        int count = engine.GetOperations().Length;

        Assert.False(history.Redo());

        Assert.Equal(count, engine.GetOperations().Length);
        Assert.Equal(default, engine.GetShapes()[0].Offset);
        Assert.False(history.CanRedo);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompensationDoesNotAuthorizeUndoAcrossForeignEdit(bool resize)
    {
        var engine = new SyncEngine(Guid.NewGuid(), Guid.NewGuid());
        var history = new EditHistory(() => engine, _ => { });
        var added = engine.AddShape(ShapeKind.Rectangle, new(1, 2), new(30, 40), 0, 2);
        if (resize)
        {
            history.RecordResize(engine.ResizeShape(added.ShapeId, new(3, 4), new(50, 60)));
        }
        else
        {
            history.RecordMove(engine.MoveObject(added.ShapeId, new(10, 20)));
        }
        var remote = new SyncEngine(Guid.NewGuid(), engine.SessionId);
        remote.MergeSnapshot(engine.CreateSnapshot());
        if (resize)
        {
            engine.Receive(remote.ResizeShape(added.ShapeId, new(3, 4), new(50, 60)));
            history.RecordResize(engine.ResizeShape(added.ShapeId, new(6, 7), new(80, 90)));
        }
        else
        {
            engine.Receive(remote.MoveObject(added.ShapeId, new(10, 20)));
            history.RecordMove(engine.MoveObject(added.ShapeId, new(30, 40)));
        }
        Assert.True(history.Undo());
        int count = engine.GetOperations().Length;

        Assert.False(history.Undo());

        Assert.Equal(count, engine.GetOperations().Length);
        var shape = Assert.Single(engine.GetShapes());
        if (resize)
        {
            Assert.Equal(new StrokePoint(3, 4), shape.Start);
            Assert.Equal(new StrokePoint(50, 60), shape.End);
        }
        else
        {
            Assert.Equal(new StrokePoint(10, 20), shape.Offset);
        }
    }

    [Fact]
    public void BeforeStateComesFromLogAndUsesLatestReceivedGeometry()
    {
        var engine = new SyncEngine(Guid.NewGuid(), Guid.NewGuid());
        var history = new EditHistory(() => engine, _ => { });
        var added = engine.AddShape(ShapeKind.Rectangle, new(1, 2), new(30, 40), 0, 2);
        var remote = new SyncEngine(Guid.NewGuid(), engine.SessionId);
        remote.MergeSnapshot(engine.CreateSnapshot());
        engine.Receive(remote.MoveObject(added.ShapeId, new(10, 20)));
        engine.Receive(remote.ResizeShape(added.ShapeId, new(3, 4), new(50, 60)));
        history.RecordMove(engine.MoveObject(added.ShapeId, new(30, 40)));
        history.RecordResize(engine.ResizeShape(added.ShapeId, new(6, 7), new(80, 90)));

        Assert.True(history.Undo());
        Assert.True(history.Undo());

        var shape = Assert.Single(engine.GetShapes());
        Assert.Equal(new StrokePoint(10, 20), shape.Offset);
        Assert.Equal(new StrokePoint(3, 4), shape.Start);
        Assert.Equal(new StrokePoint(50, 60), shape.End);
    }

    [Theory]
    [InlineData("move")]
    [InlineData("resize")]
    [InlineData("layer")]
    public void ForeignEditPresentBeforeCreationRecordCannotBeUndone(string property)
    {
        var engine = new SyncEngine(Guid.NewGuid(), Guid.NewGuid());
        var history = new EditHistory(() => engine, _ => { });
        var added = engine.AddShape(ShapeKind.Line, new(1, 2), new(30, 40), 0, 2);
        var remote = new SyncEngine(Guid.NewGuid(), engine.SessionId);
        remote.MergeSnapshot(engine.CreateSnapshot());
        switch (property)
        {
            case "move":
                engine.Receive(remote.MoveObject(added.ShapeId, new(10, 20)));
                break;
            case "resize":
                engine.Receive(remote.ResizeShape(added.ShapeId, new(3, 4), new(50, 60)));
                break;
            case "layer":
                var layer = remote.AddLayer("Other");
                engine.Receive(layer);
                engine.Receive(remote.MoveObjectToLayer(added.ShapeId, layer.LayerId));
                break;
        }
        history.RecordCreation(added.ShapeId);
        int count = engine.GetOperations().Length;

        Assert.False(history.Undo());

        Assert.Equal(count, engine.GetOperations().Length);
        Assert.Single(engine.GetShapes());
    }

    [Fact]
    public void DeletedLayerPreventsCreationRedoWithoutNewOperations()
    {
        var engine = new SyncEngine(Guid.NewGuid(), Guid.NewGuid());
        var history = new EditHistory(() => engine, _ => { });
        var layer = engine.AddLayer("Other");
        var added = engine.AddShape(ShapeKind.Line, new(1, 2), new(30, 40), 0, 2, layerId: layer.LayerId);
        history.RecordCreation(added.ShapeId);
        Assert.True(history.Undo());
        engine.RemoveLayer(layer.LayerId);
        int count = engine.GetOperations().Length;

        Assert.False(history.Redo());

        Assert.Equal(count, engine.GetOperations().Length);
        Assert.Empty(engine.GetShapes());
    }

    [Fact]
    public void HistoryOnlyAppendsOperationsAndReplicasConverge()
    {
        var engine = new SyncEngine(Guid.NewGuid(), Guid.NewGuid());
        var published = new List<Operation>();
        var history = new EditHistory(() => engine, published.Add);
        var added = engine.AddShape(ShapeKind.Ellipse, new(1, 2), new(30, 40), 0, 2);
        history.RecordCreation(added.ShapeId);
        history.RecordMove(engine.MoveObject(added.ShapeId, new(10, 20)));
        var beforeIds = engine.GetOperations().Select(operation => operation.Stamp.OperationId).ToArray();
        Assert.True(history.Undo());
        Assert.True(history.Undo());
        Assert.True(history.Redo());
        Assert.True(history.Redo());

        var allIds = engine.GetOperations().Select(operation => operation.Stamp.OperationId).ToArray();
        Assert.Equal(beforeIds, allIds.Take(beforeIds.Length));
        Assert.All(published, operation => Assert.Contains(operation.Stamp.OperationId, allIds));
        var replica = new SyncEngine(Guid.NewGuid(), engine.SessionId);
        foreach (var operation in engine.GetOperations().Reverse())
        {
            replica.Receive(operation);
        }
        Assert.Equal(JsonSerializer.Serialize(engine.GetShapes()), JsonSerializer.Serialize(replica.GetShapes()));
        Assert.Equal(engine.GetTombstones(), replica.GetTombstones());
        history.Clear();
        Assert.False(history.CanUndo);
        Assert.False(history.CanRedo);
        Assert.Equal(allIds.Length, engine.GetOperations().Length);
    }
}
