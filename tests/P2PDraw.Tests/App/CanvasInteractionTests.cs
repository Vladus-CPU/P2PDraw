using Avalonia;
using P2PDraw.App.Controls;
using P2PDraw.App.Interaction;
using P2PDraw.Core.Models;
using P2PDraw.Core.Sync;
using Xunit;

namespace P2PDraw.Tests.App;

public class CanvasInteractionTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BrushFinishesOnceWhenCommittedOrCanceled(bool commit)
    {
        var interaction = new CanvasInteraction();
        var started = new List<StrokePoint>();
        var continued = new List<StrokePoint>();
        int finished = 0;
        interaction.StrokeStarted += started.Add;
        interaction.StrokeContinued += continued.Add;
        interaction.StrokeFinished += () => finished++;

        interaction.Begin(new(10, 20), 6, 7);
        interaction.Continue(new(30, 40));
        Assert.True(interaction.IsActive);
        interaction.Finish(commit);
        interaction.Finish(commit);

        Assert.Equal(new StrokePoint(10, 20), Assert.Single(started));
        Assert.Equal(new StrokePoint(30, 40), Assert.Single(continued));
        Assert.Equal(1, finished);
        Assert.False(interaction.IsActive);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MovePreviewLeavesDocumentUnchangedAndOnlyCommitRaisesMove(bool commit)
    {
        var engine = new SyncEngine(Guid.NewGuid(), Guid.NewGuid());
        var added = engine.AddShape(ShapeKind.Rectangle, new(0, 0), new(100, 100), 0xFF112233, 2, 0xFF112233);
        engine.MoveObject(added.ShapeId, new(10, 20));
        var original = Assert.Single(engine.GetShapes());
        int operationCount = engine.GetOperations().Length;
        var interaction = new CanvasInteraction();
        interaction.UpdateDocument(engine.GetStrokes(), engine.GetShapes(), null, null);
        interaction.ChangeTool(DrawingTool.Select);
        var moves = new List<(Guid ObjectId, StrokePoint Offset)>();
        interaction.ObjectMoved += (objectId, offset) => moves.Add((objectId, offset));

        interaction.Begin(new(60, 70), 6, 7);
        interaction.Continue(new(80, 100));

        Assert.Equal(original.ShapeId, interaction.SelectedObjectId);
        Assert.Equal(new StrokePoint(30, 50), interaction.DisplayOffset(original.ShapeId, original.Offset));
        Assert.Equal(new Rect(30, 50, 100, 100), interaction.FindBounds(original.ShapeId));
        Assert.Equal(original, Assert.Single(engine.GetShapes()));
        Assert.Equal(operationCount, engine.GetOperations().Length);
        Assert.Empty(moves);

        interaction.Finish(commit);
        interaction.Finish(commit);

        if (commit)
        {
            Assert.Equal((original.ShapeId, new StrokePoint(30, 50)), Assert.Single(moves));
        }
        else
        {
            Assert.Empty(moves);
        }
        Assert.Equal(original.Offset, interaction.DisplayOffset(original.ShapeId, original.Offset));
        Assert.Equal(original, Assert.Single(engine.GetShapes()));
        Assert.Equal(operationCount, engine.GetOperations().Length);
        Assert.False(interaction.IsActive);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ResizePreviewUsesLocalCoordinatesAndOnlyCommitRaisesResize(bool commit)
    {
        var engine = new SyncEngine(Guid.NewGuid(), Guid.NewGuid());
        var added = engine.AddShape(ShapeKind.Rectangle, new(10, 20), new(110, 120), 0xFF112233, 2, 0xFF112233);
        engine.MoveObject(added.ShapeId, new(5, 10));
        var original = Assert.Single(engine.GetShapes());
        int operationCount = engine.GetOperations().Length;
        var interaction = new CanvasInteraction();
        interaction.UpdateDocument(engine.GetStrokes(), engine.GetShapes(), null, null);
        interaction.ChangeTool(DrawingTool.Select);
        interaction.Begin(new(65, 80), 6, 7);
        interaction.Finish(true);
        var resizes = new List<(Guid ShapeId, StrokePoint Start, StrokePoint End)>();
        interaction.ShapeResized += (shapeId, start, end) => resizes.Add((shapeId, start, end));
        int moves = 0;
        interaction.ObjectMoved += (_, _) => moves++;

        interaction.Begin(new(15, 30), 6, 7);
        interaction.Continue(new(-5, 0));
        var preview = interaction.DisplayShape(original);

        Assert.Equal(new StrokePoint(-10, -10), preview.Start);
        Assert.Equal(original.End, preview.End);
        Assert.Equal(original.Offset, preview.Offset);
        Assert.Equal(new Rect(-5, 0, 120, 130), interaction.FindBounds(original.ShapeId));
        Assert.Equal(original, Assert.Single(engine.GetShapes()));
        Assert.Equal(operationCount, engine.GetOperations().Length);
        Assert.Empty(resizes);

        interaction.Finish(commit);
        interaction.Finish(commit);

        if (commit)
        {
            Assert.Equal((original.ShapeId, preview.Start, preview.End), Assert.Single(resizes));
        }
        else
        {
            Assert.Empty(resizes);
        }
        Assert.Equal(0, moves);
        Assert.Equal(original, interaction.DisplayShape(original));
        Assert.Equal(original, Assert.Single(engine.GetShapes()));
        Assert.Equal(operationCount, engine.GetOperations().Length);
        Assert.False(interaction.IsActive);
    }

    [Theory]
    [InlineData(DrawingTool.Line, ShapeKind.Line)]
    [InlineData(DrawingTool.Rectangle, ShapeKind.Rectangle)]
    [InlineData(DrawingTool.Ellipse, ShapeKind.Ellipse)]
    public void ShapeDragProducesOneCreationWithDocumentCoordinates(DrawingTool tool, ShapeKind kind)
    {
        var interaction = new CanvasInteraction();
        interaction.ChangeTool(tool);
        var created = new List<(ShapeKind Kind, StrokePoint Start, StrokePoint End)>();
        interaction.ShapeCreated += (shapeKind, start, end) => created.Add((shapeKind, start, end));

        interaction.Begin(new(10, 20), 3, 3.5);
        interaction.Continue(new(30, 40));
        Assert.Empty(created);
        interaction.Finish(true);
        interaction.Finish(true);

        Assert.Equal((kind, new StrokePoint(10, 20), new StrokePoint(30, 40)), Assert.Single(created));
        Assert.False(interaction.IsActive);
    }

    [Theory]
    [InlineData(false, 30, 40)]
    [InlineData(true, 10, 20)]
    public void CanceledOrStationaryShapeDoesNotCreateAnObject(bool commit, double endX, double endY)
    {
        var interaction = new CanvasInteraction();
        interaction.ChangeTool(DrawingTool.Rectangle);
        int created = 0;
        interaction.ShapeCreated += (_, _, _) => created++;

        interaction.Begin(new(10, 20), 6, 7);
        interaction.Continue(new(endX, endY));
        interaction.Finish(commit);

        Assert.Equal(0, created);
        Assert.False(interaction.IsActive);
    }

    [Fact]
    public void HitTestingUsesLayerOrderAndSkipsLockedOrHiddenLayers()
    {
        var engine = new SyncEngine(Guid.NewGuid(), Guid.NewGuid());
        var upperLayer = engine.AddLayer("Верхній");
        var lower = engine.AddShape(ShapeKind.Rectangle, new(0, 0), new(100, 100), 0xFF112233, 2, 0xFF112233);
        var upper = engine.AddShape(ShapeKind.Rectangle, new(0, 0), new(100, 100), 0xFF112233, 2, 0xFF112233, upperLayer.LayerId);
        var interaction = new CanvasInteraction();
        var selectable = new HashSet<Guid> { Layer.DefaultLayerId, upperLayer.LayerId };
        var point = new StrokePoint(50, 50);

        interaction.UpdateDocument(engine.GetStrokes(), engine.GetShapes(), [Layer.DefaultLayerId, upperLayer.LayerId], selectable);
        Assert.Equal(upper.ShapeId, interaction.HitTestObject(point, 1));

        interaction.UpdateDocument(engine.GetStrokes(), engine.GetShapes(), [upperLayer.LayerId, Layer.DefaultLayerId], selectable);
        Assert.Equal(lower.ShapeId, interaction.HitTestObject(point, 1));

        interaction.UpdateDocument(engine.GetStrokes(), engine.GetShapes(), [Layer.DefaultLayerId, upperLayer.LayerId], new HashSet<Guid> { Layer.DefaultLayerId });
        Assert.Equal(lower.ShapeId, interaction.HitTestObject(point, 1));
        Assert.False(interaction.CanSelectObject(upper.ShapeId));

        interaction.UpdateDocument(engine.GetStrokes(), engine.GetShapes(), [upperLayer.LayerId], selectable);
        Assert.Equal(upper.ShapeId, interaction.HitTestObject(point, 1));
        Assert.False(interaction.CanSelectObject(lower.ShapeId));
    }
}
