using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using P2PDraw.App.Controls;
using P2PDraw.App.Interaction;
using P2PDraw.Core.Models;
using P2PDraw.Core.Operations;
using Xunit;

namespace P2PDraw.Tests.App;

public partial class EditorTests
{
    [Theory]
    [InlineData(ShapeKind.Line, false)]
    [InlineData(ShapeKind.Rectangle, false)]
    [InlineData(ShapeKind.Ellipse, false)]
    [InlineData(ShapeKind.Line, true)]
    [InlineData(ShapeKind.Rectangle, true)]
    [InlineData(ShapeKind.Ellipse, true)]
    public async Task ResizeHandlesUseDocumentCoordinatesAndCommitOnce(ShapeKind kind, bool touch)
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(EditorTests));
        await session.Dispatch(() =>
        {
            var stamp = new OperationStamp(Guid.NewGuid(), Guid.NewGuid(), 1, 1);
            var shape = new Shape(Guid.NewGuid(), stamp.PeerId, kind, new(30, 40), new(90, 100),
                0xFF112233, 4, stamp, 0xFF223344)
            {
                Offset = new(5, 7)
            };
            var canvas = new DrawingCanvas { Shapes = [shape], Tool = DrawingTool.Select };
            var changes = new List<Shape>();
            int moves = 0;
            canvas.ShapeResized += (objectId, start, end) =>
            {
                Assert.Equal(shape.ShapeId, objectId);
                changes.Add(shape with { Start = start, End = end });
            };
            canvas.ObjectMoved += (_, _) => moves++;
            var window = new Window { Content = canvas, Width = 600, Height = 500 };
            window.Show();
            window.UpdateLayout();
            canvas.ZoomAt(2, default);
            canvas.PanBy(new Vector(20, 10));
            Point center = canvas.DocumentToScreen(new(65, 77));
            window.MouseDown(center, MouseButton.Left);
            window.MouseUp(center, MouseButton.Left);
            Assert.Equal(shape.ShapeId, canvas.SelectedObjectId);
            Point endpoint = canvas.DocumentToScreen(new(95, 107));
            Point target = endpoint + new Vector(40, 30);

            if (touch)
            {
                using var finger = window.TouchBegin(endpoint + new Vector(12, 0));
                window.TouchMove(finger, target);
                Assert.Empty(changes);
                Assert.Equal(shape, Assert.Single(canvas.Shapes!));
                window.TouchEnd(finger, target);
            }
            else
            {
                window.MouseDown(endpoint, MouseButton.Left);
                window.MouseMove(target, RawInputModifiers.LeftMouseButton);
                Assert.Empty(changes);
                Assert.Equal(shape, Assert.Single(canvas.Shapes!));
                window.MouseUp(target, MouseButton.Left);
            }

            var resized = Assert.Single(changes);
            Assert.Equal(new Rect(35, 47, 80, 75), CanvasGeometry.GetBounds(resized));
            Assert.Equal(shape.Offset, resized.Offset);
            Assert.Equal(shape.Color, resized.Color);
            Assert.Equal(shape.Width, resized.Width);
            Assert.Equal(shape.Fill, resized.Fill);
            Assert.Equal(0, moves);
            window.Close();
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData("removed")]
    [InlineData("hidden")]
    [InlineData("locked")]
    [InlineData("tool")]
    public async Task InterruptedResizeDoesNotPublishGeometry(string interruption)
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(EditorTests));
        await session.Dispatch(() =>
        {
            var stamp = new OperationStamp(Guid.NewGuid(), Guid.NewGuid(), 1, 1);
            var shape = new Shape(Guid.NewGuid(), stamp.PeerId, ShapeKind.Rectangle,
                new(20, 20), new(80, 80), 0, 2, stamp, 0xFF112233);
            var canvas = new DrawingCanvas { Shapes = [shape], Tool = DrawingTool.Select };
            int resized = 0;
            int moved = 0;
            canvas.ShapeResized += (_, _, _) => resized++;
            canvas.ObjectMoved += (_, _) => moved++;
            var window = new Window { Content = canvas, Width = 500, Height = 400 };
            window.Show();
            window.UpdateLayout();
            window.MouseDown(new Point(50, 50), MouseButton.Left);
            window.MouseUp(new Point(50, 50), MouseButton.Left);
            window.MouseDown(new Point(80, 80), MouseButton.Left);
            window.MouseMove(new Point(100, 110), RawInputModifiers.LeftMouseButton);

            switch (interruption)
            {
                case "removed":
                    canvas.Shapes = [];
                    break;
                case "hidden":
                    canvas.VisibleLayerIds = [];
                    break;
                case "locked":
                    canvas.SelectableLayerIds = new HashSet<Guid>();
                    break;
                case "tool":
                    canvas.Tool = DrawingTool.Brush;
                    break;
            }
            window.MouseUp(new Point(100, 110), MouseButton.Left);
            Assert.Equal(0, resized);
            Assert.Equal(0, moved);
            Assert.Null(canvas.SelectedObjectId);
            window.Close();
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public void ResizeGeometrySupportsEveryCornerAndCrossingTheOppositeCorner()
    {
        var stamp = new OperationStamp(Guid.NewGuid(), Guid.NewGuid(), 1, 1);
        var shape = new Shape(Guid.NewGuid(), stamp.PeerId, ShapeKind.Rectangle,
            new(80, 90), new(20, 30), 0, 2, stamp)
        {
            Offset = new(-3, 5)
        };
        var handles = CanvasGeometry.GetResizeHandles(shape);
        Assert.Equal(new StrokePoint[] { new(17, 35), new(77, 35), new(77, 95), new(17, 95) }, handles);
        for (int index = 0; index < handles.Length; index++)
        {
            var opposite = handles[(index + 2) % 4];
            var target = new StrokePoint(opposite.X + 10, opposite.Y + 20);
            var resized = CanvasGeometry.Resize(shape, index, target);
            Assert.Equal(new Rect(opposite.X, opposite.Y, 10, 20), CanvasGeometry.GetBounds(resized));
        }
        Assert.Equal(2, CanvasGeometry.GetResizeHandles(shape with { Kind = ShapeKind.Line }).Length);
    }
}
