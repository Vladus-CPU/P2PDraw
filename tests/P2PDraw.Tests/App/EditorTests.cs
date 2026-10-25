using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using P2PDraw.App.ViewModels;
using Xunit;

namespace P2PDraw.Tests.App;

public class EditorTests
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<P2PDraw.App.App>().UseHeadless(new AvaloniaHeadlessPlatformOptions());

    [Fact]
    public async Task LocalDrawingUsesCoreAndLocksSession()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(EditorTests));
        await session.Dispatch(async () =>
        {
            await using var model = new MainWindowViewModel();
            model.BrushColor = 0xFFFF0000;
            model.BrushWidth = 7;
            Assert.True(model.BeginStroke(new(10, 20)));
            Assert.Single(model.Strokes);
            model.AppendStroke(new(30, 40));
            model.EndStroke();

            var stroke = Assert.Single(model.GetSnapshot().Strokes);
            Assert.Equal(0xFFFF0000u, stroke.Color);
            Assert.Equal(7, stroke.Width);
            Assert.Equal(2, stroke.Points.Length);
            Assert.True(stroke.IsComplete);
            Assert.False(model.CanEditSession);
            Assert.Equal(4, model.OperationCount);
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task InvalidSessionDoesNotCreateOperationsOrListener()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(EditorTests));
        await session.Dispatch(async () =>
        {
            await using var model = new MainWindowViewModel(["0", "invalid", "1"]);
            Assert.False(model.BeginStroke(new(1, 1)));
            await model.StartListeningAsync();
            Assert.Empty(model.GetSnapshot().Operations);
            Assert.True(model.CanEditSession);
            Assert.False(model.CanConnect);
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task BusyPortCanBeCorrectedWithoutLosingDrawing()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(EditorTests));
        await session.Dispatch(async () =>
        {
            var listener = new TcpListener(IPAddress.Any, 0);
            listener.Start();
            try
            {
                string port = ((IPEndPoint)listener.LocalEndpoint).Port.ToString();
                await using var model = new MainWindowViewModel([port, Guid.NewGuid().ToString(), "1"]);
                model.BeginStroke(new(1, 1));
                model.EndStroke();
                await model.StartListeningAsync();
                Assert.True(model.CanStartListening);
                Assert.Single(model.Strokes);
                model.LocalPort = "0";
                await model.StartListeningAsync();
                Assert.True(model.CanConnect);
                Assert.False(model.CanStartListening);
                Assert.Single(model.Strokes);
            }
            finally
            {
                listener.Stop();
            }
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task TwoEditorsConvergeAfterOfflineClearAndDrawing()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(EditorTests));
        await session.Dispatch(async () =>
        {
            string sessionId = Guid.NewGuid().ToString();
            await using var first = new MainWindowViewModel(["0", sessionId, "1"]);
            await using var second = new MainWindowViewModel(["0", sessionId, "1"]);
            bool updatedOnUiThread = true;
            second.PropertyChanged += (_, _) => updatedOnUiThread &= Dispatcher.UIThread.CheckAccess();
            await first.StartListeningAsync();
            await second.StartListeningAsync();
            first.RemotePort = second.LocalPort;
            await first.ConnectAsync();
            await WaitUntilAsync(() => first.ConnectedPeers == 1 && second.ConnectedPeers == 1);

            first.BeginStroke(new(1, 2));
            second.BeginStroke(new(3, 4));
            first.AppendStroke(new(5, 6));
            second.AppendStroke(new(7, 8));
            first.EndStroke();
            second.EndStroke();
            await WaitUntilAsync(() => first.OperationCount == 8 && second.OperationCount == 8);
            Assert.Equal(Normalize(first), Normalize(second));

            first.Disconnect();
            await WaitUntilAsync(() => first.ConnectedPeers == 0 && second.ConnectedPeers == 0);
            first.ClearCanvas();
            second.BeginStroke(new(9, 10));
            second.AppendStroke(new(11, 12));
            second.EndStroke();
            await first.ConnectAsync();
            await WaitUntilAsync(() => first.OperationCount == 13 && second.OperationCount == 13);
            Assert.Equal(Normalize(first), Normalize(second));
            Assert.Single(first.Strokes);
            Assert.Equal(2, first.GetSnapshot().Tombstones.Length);
            Assert.True(updatedOnUiThread);
            return true;
        }, TestContext.Current.CancellationToken);
    }

    private static string Normalize(MainWindowViewModel model)
    {
        var snapshot = model.GetSnapshot();
        return JsonSerializer.Serialize(new { snapshot.Strokes, snapshot.Tombstones, snapshot.PendingStrokeIds, snapshot.Operations });
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }
}
