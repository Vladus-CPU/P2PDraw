using Avalonia.Headless;
using P2PDraw.App.ViewModels;
using P2PDraw.Core.Models;
using Xunit;

namespace P2PDraw.Tests.App;

public partial class EditorTests
{
    [Theory]
    [InlineData(ShapeKind.Line)]
    [InlineData(ShapeKind.Rectangle)]
    [InlineData(ShapeKind.Ellipse)]
    public async Task DuplicatePreservesShapeStyleLayerAndGeometry(ShapeKind kind)
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(EditorTests));
        await session.Dispatch(async () =>
        {
            await using var model = new MainWindowViewModel();
            model.LayerEditor.AddLayer();
            model.FillShapes = true;
            model.BrushColor = 0xAA123456;
            model.BrushWidth = 7;
            model.AddShape(kind, new(10, 20), new(30, 40));
            var original = Assert.Single(model.Shapes);
            model.MoveObject(original.ShapeId, new(100, 200));

            model.DuplicateObject(original.ShapeId);

            var copy = model.Shapes.Single(shape => shape.ShapeId != original.ShapeId);
            Assert.Equal(original.Start, copy.Start);
            Assert.Equal(original.End, copy.End);
            Assert.Equal(original.Kind, copy.Kind);
            Assert.Equal(original.Color, copy.Color);
            Assert.Equal(original.Fill, copy.Fill);
            Assert.Equal(original.Width, copy.Width);
            Assert.Equal(original.LayerId, copy.LayerId);
            Assert.Equal(new StrokePoint(116, 216), copy.Offset);
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task DuplicateStrokeUsesNewIdAndIndependentPoints()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(EditorTests));
        await session.Dispatch(async () =>
        {
            await using var model = new MainWindowViewModel();
            model.BeginStroke(new(10, 20));
            model.AppendStroke(new(30, 40));
            model.EndStroke();
            var original = Assert.Single(model.Strokes);
            model.DuplicateObject(original.StrokeId);
            var copy = model.Strokes.Single(stroke => stroke.StrokeId != original.StrokeId);
            Assert.Equal(original.Points, copy.Points);
            Assert.Equal(original.Color, copy.Color);
            Assert.Equal(original.Width, copy.Width);
            Assert.Equal(original.LayerId, copy.LayerId);
            Assert.True(copy.IsComplete);
            Assert.Equal(new StrokePoint(16, 16), copy.Offset);
            model.RemoveObject(original.StrokeId);
            Assert.Equal(copy.StrokeId, Assert.Single(model.Strokes).StrokeId);
            return true;
        }, TestContext.Current.CancellationToken);
    }
}
