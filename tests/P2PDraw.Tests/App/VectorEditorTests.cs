using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using P2PDraw.App.Controls;
using P2PDraw.App.Interaction;
using P2PDraw.App.ViewModels;
using P2PDraw.App.Views;
using P2PDraw.Core.Models;
using Xunit;

namespace P2PDraw.Tests.App;

public partial class EditorTests
{
    [Fact]
    public async Task TouchShapesAndStrokeMovesSynchronizeBetweenEditors()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(EditorTests));
        await session.Dispatch(async () =>
        {
            string sessionId = Guid.NewGuid().ToString();
            await using var mobile = new MainWindowViewModel(["0", sessionId, "1"]);
            await using var desktop = new MainWindowViewModel(["0", sessionId, "1"]);
            var view = new MainView { DataContext = mobile };
            var window = new Window { Content = view, Width = 412, Height = 915 };
            try
            {
                window.Show();
                window.UpdateLayout();
                await mobile.StartListeningAsync();
                await desktop.StartListeningAsync();
                mobile.RemotePort = desktop.LocalPort;
                await mobile.ConnectAsync();
                await WaitUntilAsync(() => mobile.ConnectedPeers == 1 && desktop.ConnectedPeers == 1);
                var canvas = view.FindControl<DrawingCanvas>("DrawingSurface")!;
                canvas.ZoomAt(2, default);
                view.FindControl<Button>("EllipseTool")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                mobile.FillShapes = true;
                Point start = canvas.TranslatePoint(new Point(40, 60), window)!.Value;
                Point end = canvas.TranslatePoint(new Point(160, 180), window)!.Value;
                using var finger = window.TouchBegin(start);
                window.TouchMove(finger, end);
                Assert.Equal(0, mobile.OperationCount);
                window.TouchEnd(finger, end);
                await WaitUntilAsync(() => desktop.OperationCount == 1);
                var shape = Assert.Single(desktop.Shapes);
                Assert.Equal(new StrokePoint(20, 30), shape.Start);
                Assert.Equal(new StrokePoint(80, 90), shape.End);
                Assert.Equal(mobile.BrushColor, shape.Fill);
                Assert.Equal(Normalize(mobile), Normalize(desktop));

                desktop.BeginStroke(new(100, 30));
                desktop.AppendStroke(new(140, 70));
                desktop.EndStroke();
                await WaitUntilAsync(() => mobile.OperationCount == 5);
                var original = Assert.Single(mobile.Strokes);
                view.FindControl<Button>("SelectTool")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Point grab = canvas.TranslatePoint(canvas.DocumentToScreen(new(120, 50)), window)!.Value;
                Point target = grab + new Vector(20, 30);
                using var moving = window.TouchBegin(grab);
                window.TouchMove(moving, target);
                Assert.Equal(5, mobile.OperationCount);
                window.TouchEnd(moving, target);
                await WaitUntilAsync(() => desktop.OperationCount == 6);
                Assert.Equal(original.StrokeId, canvas.SelectedObjectId);
                Assert.Equal(new StrokePoint(10, 15), Assert.Single(desktop.Strokes).Offset);
                Assert.Equal(original.Points, desktop.Strokes[0].Points);
                Assert.Equal(Normalize(mobile), Normalize(desktop));

                Point empty = canvas.TranslatePoint(new Point(10, 300), window)!.Value;
                using var deselecting = window.TouchBegin(empty);
                window.TouchEnd(deselecting, empty);
                Assert.Null(canvas.SelectedObjectId);
                Assert.False(view.FindControl<Button>("DeleteButton")!.IsEnabled);
                Assert.Equal(6, mobile.OperationCount);

                mobile.Disconnect();
                await WaitUntilAsync(() => mobile.ConnectedPeers == 0 && desktop.ConnectedPeers == 0);
                mobile.ClearCanvas();
                desktop.AddShape(ShapeKind.Rectangle, new(5, 6), new(7, 8));
                await mobile.ConnectAsync();
                await WaitUntilAsync(() => mobile.OperationCount == 8 && desktop.OperationCount == 8);
                Assert.Equal(Normalize(mobile), Normalize(desktop));
                Assert.Empty(mobile.Strokes);
                Assert.Equal(ShapeKind.Rectangle, Assert.Single(mobile.Shapes).Kind);
                Assert.Equal(2, mobile.GetSnapshot().Tombstones.Length);
            }
            finally
            {
                await mobile.DisposeAsync();
                await desktop.DisposeAsync();
                window.Close();
            }
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(ShapeKind.Line, "LineTool")]
    [InlineData(ShapeKind.Rectangle, "RectangleTool")]
    [InlineData(ShapeKind.Ellipse, "EllipseTool")]
    public async Task ShapePreviewSelectionMoveAndDeleteUseDocumentCoordinates(ShapeKind kind, string toolName)
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(EditorTests));
        await session.Dispatch(async () =>
        {
            await using var model = new MainWindowViewModel();
            var window = new MainWindow { DataContext = model };
            window.Show();
            window.UpdateLayout();
            var view = window.Editor;
            var canvas = view.FindControl<DrawingCanvas>("DrawingSurface")!;
            canvas.ZoomAt(2, default);
            canvas.PanBy(new Vector(20, 10));
            view.FindControl<Button>(toolName)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var start = canvas.DocumentToScreen(new(30, 40));
            var end = canvas.DocumentToScreen(new(90, 100));
            Point windowStart = canvas.TranslatePoint(start, window)!.Value;
            Point windowEnd = canvas.TranslatePoint(end, window)!.Value;
            window.MouseDown(windowStart, MouseButton.Left);
            window.MouseMove(windowEnd, RawInputModifiers.LeftMouseButton);
            Assert.Empty(model.GetSnapshot().Shapes);
            Assert.Equal(0, model.OperationCount);
            window.MouseUp(windowEnd, MouseButton.Left);

            var shape = Assert.Single(model.Shapes);
            Assert.Equal(kind, shape.Kind);
            Assert.Equal(new StrokePoint(30, 40), shape.Start);
            Assert.Equal(new StrokePoint(90, 100), shape.End);
            Assert.Equal(1, model.OperationCount);
            Assert.Equal(1, model.ObjectCount);
            Assert.Contains("selected", view.FindControl<Button>(toolName)!.Classes);

            view.FindControl<Button>("SelectTool")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var grab = new StrokePoint(60, 70);
            if (kind == ShapeKind.Rectangle)
            {
                grab = new(30, 70);
            }
            else if (kind == ShapeKind.Ellipse)
            {
                grab = new(60, 40);
            }
            Point grabPoint = canvas.TranslatePoint(canvas.DocumentToScreen(grab), window)!.Value;
            Point targetPoint = grabPoint + new Vector(20, 30);
            window.MouseDown(grabPoint, MouseButton.Left);
            window.MouseMove(targetPoint, RawInputModifiers.LeftMouseButton);
            Assert.Equal(1, model.OperationCount);
            window.MouseUp(targetPoint, MouseButton.Left);
            Assert.Equal(shape.ShapeId, canvas.SelectedObjectId);
            Assert.Equal(new StrokePoint(10, 15), model.Shapes[0].Offset);
            Assert.Equal(shape.Start, model.Shapes[0].Start);
            Assert.Equal(2, model.OperationCount);

            var delete = view.FindControl<Button>("DeleteButton")!;
            Assert.True(delete.IsEnabled);
            delete.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Empty(model.Shapes);
            Assert.Null(canvas.SelectedObjectId);
            Assert.Contains(shape.ShapeId, model.GetSnapshot().Tombstones);
            Assert.Equal(3, model.OperationCount);
            await model.DisposeAsync();
            window.Close();
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public void HitTestingUsesContoursFillAndTranslatedStrokeGeometry()
    {
        var stamp = new P2PDraw.Core.Operations.OperationStamp(Guid.NewGuid(), Guid.NewGuid(), 1, 1);
        var rectangle = new Shape(Guid.NewGuid(), stamp.PeerId, ShapeKind.Rectangle, new(100, 80), new(20, 10), 0, 2, stamp);
        Assert.Equal(new Rect(20, 10, 80, 70), CanvasGeometry.GetBounds(rectangle));
        Assert.True(CanvasGeometry.Contains(rectangle, new(21, 40), 2));
        Assert.False(CanvasGeometry.Contains(rectangle, new(60, 40), 2));
        Assert.True(CanvasGeometry.Contains(rectangle with { Fill = 0xFF112233 }, new(60, 40), 2));
        var ellipse = rectangle with { Kind = ShapeKind.Ellipse };
        Assert.True(CanvasGeometry.Contains(ellipse, new(60, 10), 2));
        Assert.False(CanvasGeometry.Contains(ellipse, new(20, 10), 2));
        Assert.False(CanvasGeometry.Contains(ellipse, new(60, 45), 2));
        Assert.True(CanvasGeometry.Contains(ellipse with { Fill = 0xFF112233 }, new(60, 45), 2));
        var stroke = new Stroke(Guid.NewGuid(), stamp.PeerId, 0, 6, stamp, [new(0, 0), new(10, 10)], true) { Offset = new(100, 200) };
        Assert.True(CanvasGeometry.Contains(stroke, new(105, 207), 1));
        Assert.False(CanvasGeometry.Contains(stroke, new(5, 7), 1));
    }
}
