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
    [Fact]
    public async Task LayerOrderVisibilityAndLocksControlObjectSelection()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(EditorTests));
        await session.Dispatch(() =>
        {
            Guid lowerLayer = Guid.NewGuid();
            Guid upperLayer = Guid.NewGuid();
            var stamp = new OperationStamp(Guid.NewGuid(), Guid.NewGuid(), 1, 1);
            var shape = new Shape(Guid.NewGuid(), stamp.PeerId, ShapeKind.Rectangle, new(20, 20), new(80, 80),
                0, 2, stamp, 0xFF112233)
            {
                LayerId = lowerLayer
            };
            var stroke = new Stroke(Guid.NewGuid(), stamp.PeerId, 0, 4, stamp, [new(20, 20), new(80, 80)], true)
            {
                LayerId = upperLayer
            };
            var canvas = new DrawingCanvas
            {
                Shapes = [shape],
                Strokes = [stroke],
                VisibleLayerIds = [lowerLayer, upperLayer],
                SelectableLayerIds = new HashSet<Guid> { lowerLayer, upperLayer }
            };

            Assert.Equal(stroke.StrokeId, canvas.HitTestObject(new(50, 50)));
            canvas.VisibleLayerIds = [upperLayer, lowerLayer];
            Assert.Equal(shape.ShapeId, canvas.HitTestObject(new(50, 50)));
            canvas.VisibleLayerIds = [lowerLayer, upperLayer];
            canvas.SelectableLayerIds = new HashSet<Guid> { lowerLayer };
            Assert.Equal(shape.ShapeId, canvas.HitTestObject(new(50, 50)));
            canvas.SelectableLayerIds = new HashSet<Guid> { upperLayer };
            canvas.VisibleLayerIds = [lowerLayer];
            Assert.Null(canvas.HitTestObject(new(50, 50)));
            Assert.Single(canvas.Shapes);
            Assert.Single(canvas.Strokes);
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task LockingOrHidingSelectedLayerCancelsLocalDrag()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(EditorTests));
        await session.Dispatch(() =>
        {
            Guid layerId = Guid.NewGuid();
            var stamp = new OperationStamp(Guid.NewGuid(), Guid.NewGuid(), 1, 1);
            var shape = new Shape(Guid.NewGuid(), stamp.PeerId, ShapeKind.Rectangle, new(20, 20), new(80, 80),
                0, 2, stamp, 0xFF112233)
            {
                LayerId = layerId
            };
            var canvas = new DrawingCanvas
            {
                Shapes = [shape],
                VisibleLayerIds = [layerId],
                Tool = DrawingTool.Select
            };
            int moves = 0;
            canvas.ObjectMoved += (_, _) => moves++;
            var window = new Window { Content = canvas, Width = 500, Height = 400 };
            window.Show();
            window.UpdateLayout();

            window.MouseDown(new Point(50, 50), MouseButton.Left);
            window.MouseMove(new Point(70, 60), RawInputModifiers.LeftMouseButton);
            Assert.Equal(shape.ShapeId, canvas.SelectedObjectId);
            canvas.SelectableLayerIds = new HashSet<Guid>();
            window.MouseUp(new Point(70, 60), MouseButton.Left);
            Assert.Null(canvas.SelectedObjectId);
            Assert.Equal(0, moves);

            canvas.SelectableLayerIds = new HashSet<Guid> { layerId };
            window.MouseDown(new Point(50, 50), MouseButton.Left);
            window.MouseUp(new Point(50, 50), MouseButton.Left);
            Assert.Equal(shape.ShapeId, canvas.SelectedObjectId);
            canvas.VisibleLayerIds = [];
            Assert.Null(canvas.SelectedObjectId);
            Assert.Single(canvas.Shapes);
            window.Close();
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task UnavailableActiveLayerBlocksDrawingButKeepsTouchNavigation()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(EditorTests));
        await session.Dispatch(() =>
        {
            var canvas = new DrawingCanvas { CanDraw = false };
            int started = 0;
            int continued = 0;
            int finished = 0;
            int created = 0;
            canvas.StrokeStarted += _ => started++;
            canvas.StrokeContinued += _ => continued++;
            canvas.StrokeFinished += () => finished++;
            canvas.ShapeCreated += (_, _, _) => created++;
            var window = new Window { Content = canvas, Width = 600, Height = 400 };
            window.Show();
            window.UpdateLayout();

            window.MouseDown(new Point(20, 20), MouseButton.Left);
            window.MouseMove(new Point(80, 80), RawInputModifiers.LeftMouseButton);
            window.MouseUp(new Point(80, 80), MouseButton.Left);
            using var drawing = window.TouchBegin(new Point(20, 20));
            window.TouchMove(drawing, new Point(80, 80));
            window.TouchEnd(drawing, new Point(80, 80));
            canvas.Tool = DrawingTool.Rectangle;
            window.MouseDown(new Point(20, 20), MouseButton.Left);
            window.MouseMove(new Point(80, 80), RawInputModifiers.LeftMouseButton);
            window.MouseUp(new Point(80, 80), MouseButton.Left);
            Assert.Equal(0, started);
            Assert.Equal(0, continued);
            Assert.Equal(0, finished);
            Assert.Equal(0, created);

            using var first = window.TouchBegin(new Point(100, 100));
            using var second = window.TouchBegin(new Point(200, 100));
            window.TouchMove(first, new Point(50, 100));
            window.TouchMove(second, new Point(250, 100));
            window.TouchEnd(first, new Point(50, 100));
            window.TouchEnd(second, new Point(250, 100));
            Assert.Equal(2, canvas.Zoom, 8);
            Assert.Equal(0, created);
            window.Close();
            return true;
        }, TestContext.Current.CancellationToken);
    }
}
