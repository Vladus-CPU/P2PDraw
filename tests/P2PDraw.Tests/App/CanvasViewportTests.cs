using Avalonia;
using Avalonia.Headless;
using P2PDraw.App.Controls;
using Xunit;

namespace P2PDraw.Tests.App;

public partial class EditorTests
{
    [Fact]
    public async Task ViewNotificationsExposeUpdatedStateWithoutReportingUnchangedOrInvalidViews()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(EditorTests));
        await session.Dispatch(() =>
        {
            var canvas = new DrawingCanvas();
            var changes = new List<(double Zoom, Vector Pan)>();
            canvas.ViewChanged += () => changes.Add((canvas.Zoom, canvas.Pan));

            canvas.ResetView();
            canvas.PanBy(default);
            canvas.ZoomAt(1, default);
            Assert.Empty(changes);

            canvas.PanBy(new Vector(20, 10));
            canvas.ZoomAt(2, default);
            canvas.ResetView();
            canvas.ResetView();

            Assert.Equal(new[] { (1d, new Vector(20, 10)), (2d, new Vector(40, 20)), (1d, default(Vector)) }, changes);
            Assert.Throws<ArgumentOutOfRangeException>(() => canvas.ZoomAt(double.NaN, default));
            Assert.Throws<ArgumentOutOfRangeException>(() => canvas.PanBy(new Vector(double.PositiveInfinity, 0)));
            Assert.Equal(3, changes.Count);
            Assert.Equal(1, canvas.Zoom);
            Assert.Equal(default, canvas.Pan);
            return true;
        }, TestContext.Current.CancellationToken);
    }
}
