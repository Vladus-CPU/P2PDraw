using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using P2PDraw.App.Controls;
using P2PDraw.App.ViewModels;
using P2PDraw.Core.Models;
using Xunit;

namespace P2PDraw.Tests.App;

public partial class EditorTests
{
    [Fact]
    public async Task ZoomKeepsCursorAnchorAndDocumentCoordinates()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(EditorTests));
        await session.Dispatch(async () =>
        {
            await using var model = new MainWindowViewModel();
            var canvas = new DrawingCanvas { Strokes = model.Strokes };
            var window = new Window { Content = canvas, Width = 600, Height = 400 };
            window.Show();
            window.UpdateLayout();
            model.BeginStroke(new(10, 20));
            model.AppendStroke(new(30, 40));
            model.EndStroke();
            var points = model.Strokes[0].Points;
            var anchor = new Point(150, 100);
            var before = canvas.ScreenToDocument(anchor);

            canvas.ZoomAt(2, anchor);
            canvas.PanBy(new Vector(30, -20));
            var screenPoint = canvas.DocumentToScreen(new StrokePoint(12, 34));
            var restoredPoint = canvas.ScreenToDocument(screenPoint);
            Assert.Equal(12, restoredPoint.X, 8);
            Assert.Equal(34, restoredPoint.Y, 8);
            canvas.PanBy(new Vector(-30, 20));
            Assert.Equal(before.X, canvas.ScreenToDocument(anchor).X, 8);
            Assert.Equal(before.Y, canvas.ScreenToDocument(anchor).Y, 8);
            Assert.Equal(2, canvas.Zoom);
            Assert.Equal(points, model.Strokes[0].Points);
            Assert.Equal(4, model.OperationCount);

            canvas.ResetView();
            Assert.Equal(1, canvas.Zoom);
            Assert.Equal(default, canvas.Pan);
            Assert.Equal(4, model.OperationCount);
            window.Close();
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task WheelAndMiddleDragChangeViewWithoutDrawingOperations()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(EditorTests));
        await session.Dispatch(async () =>
        {
            await using var model = new MainWindowViewModel();
            var canvas = new DrawingCanvas();
            canvas.StrokeStarted += point => model.BeginStroke(point);
            canvas.StrokeContinued += model.AppendStroke;
            canvas.StrokeFinished += model.EndStroke;
            var window = new Window { Content = canvas, Width = 600, Height = 400 };
            window.Show();
            window.UpdateLayout();
            var anchor = new Point(200, 150);
            window.MouseWheel(anchor, new Vector(0, 1));
            Assert.Equal(1.2, canvas.Zoom, 8);
            Assert.Equal(200, canvas.ScreenToDocument(anchor).X, 8);
            Assert.Equal(150, canvas.ScreenToDocument(anchor).Y, 8);
            var initialPan = canvas.Pan;

            window.MouseDown(new Point(100, 100), MouseButton.Middle);
            window.MouseMove(new Point(140, 120), RawInputModifiers.MiddleMouseButton);
            window.MouseUp(new Point(140, 120), MouseButton.Middle);
            Assert.Equal(initialPan + new Vector(40, 20), canvas.Pan);
            Assert.Equal(0, model.OperationCount);

            var start = canvas.ScreenToDocument(new Point(250, 200));
            var end = canvas.ScreenToDocument(new Point(300, 240));
            window.MouseDown(new Point(250, 200), MouseButton.Left);
            window.MouseMove(new Point(300, 240), RawInputModifiers.LeftMouseButton);
            window.MouseUp(new Point(300, 240), MouseButton.Left);
            var stroke = Assert.Single(model.Strokes);
            Assert.Equal(new[] { start, end }, stroke.Points);
            Assert.True(stroke.IsComplete);
            window.Close();
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task TwoFingerPinchAndPanDoNotCreateStrayStrokes()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(EditorTests));
        await session.Dispatch(async () =>
        {
            await using var model = new MainWindowViewModel();
            var canvas = new DrawingCanvas();
            canvas.StrokeStarted += point => model.BeginStroke(point);
            canvas.StrokeContinued += model.AppendStroke;
            canvas.StrokeFinished += model.EndStroke;
            var window = new Window { Content = canvas, Width = 600, Height = 400 };
            window.Show();
            window.UpdateLayout();
            using var first = window.TouchBegin(new Point(100, 100));
            using var second = window.TouchBegin(new Point(200, 100));
            window.TouchMove(first, new Point(50, 120));
            window.TouchMove(second, new Point(250, 120));

            Assert.Equal(2, canvas.Zoom, 8);
            var center = canvas.ScreenToDocument(new Point(150, 120));
            Assert.Equal(150, center.X, 8);
            Assert.Equal(100, center.Y, 8);
            Assert.Equal(0, model.OperationCount);
            window.TouchEnd(first, new Point(50, 120));
            window.TouchMove(second, new Point(270, 130));
            window.TouchEnd(second, new Point(270, 130));
            Assert.Equal(0, model.OperationCount);

            using var drawingFinger = window.TouchBegin(new Point(300, 200));
            window.TouchMove(drawingFinger, new Point(350, 250));
            window.TouchEnd(drawingFinger, new Point(350, 250));
            var stroke = Assert.Single(model.Strokes);
            Assert.Equal(canvas.ScreenToDocument(new Point(300, 200)), stroke.Points[0]);
            Assert.Equal(canvas.ScreenToDocument(new Point(350, 250)), stroke.Points[1]);
            Assert.True(stroke.IsComplete);
            window.Close();
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ZoomLimitsAndInvalidValuesKeepViewFinite()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(EditorTests));
        await session.Dispatch(() =>
        {
            var canvas = new DrawingCanvas();
            canvas.ZoomAt(1000, new Point(100, 100));
            Assert.Equal(8, canvas.Zoom);
            canvas.ZoomAt(0.0001, new Point(100, 100));
            Assert.Equal(0.1, canvas.Zoom);
            var pan = canvas.Pan;
            Assert.Throws<ArgumentOutOfRangeException>(() => canvas.ZoomAt(double.NaN, default));
            Assert.Throws<ArgumentOutOfRangeException>(() => canvas.ZoomAt(0, default));
            Assert.Throws<ArgumentOutOfRangeException>(() => canvas.PanBy(new Vector(double.PositiveInfinity, 0)));
            Assert.Equal(pan, canvas.Pan);
            return true;
        }, TestContext.Current.CancellationToken);
    }
}
