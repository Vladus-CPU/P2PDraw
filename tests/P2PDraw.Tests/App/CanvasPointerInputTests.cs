using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using P2PDraw.App.Controls;
using P2PDraw.App.Interaction;
using P2PDraw.Core.Models;
using P2PDraw.Core.Sync;
using Xunit;

namespace P2PDraw.Tests.App;

public partial class EditorTests
{
    [Fact]
    public async Task IgnoredMousePressDoesNotChangeTouchSelectionTolerance()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(EditorTests));
        await session.Dispatch(() =>
        {
            var engine = new SyncEngine(Guid.NewGuid(), Guid.NewGuid());
            var added = engine.AddShape(ShapeKind.Line, new(20, 40), new(100, 40), 0xFF112233, 2);
            var canvas = new DrawingCanvas { Shapes = engine.GetShapes(), Tool = DrawingTool.Select };
            var window = new Window { Content = canvas, Width = 400, Height = 300 };
            window.Show();
            window.UpdateLayout();

            using var finger = window.TouchBegin(new Point(50, 50));
            window.MouseDown(new Point(200, 200), MouseButton.Left);
            window.TouchMove(finger, new Point(54, 50));

            Assert.Equal(added.ShapeId, canvas.SelectedObjectId);
            Assert.Equal(1, canvas.Zoom);
            window.MouseUp(new Point(200, 200), MouseButton.Left);
            window.TouchEnd(finger, new Point(54, 50));
            Assert.Single(engine.GetOperations());
            window.Close();
            return true;
        }, TestContext.Current.CancellationToken);
    }
}
