using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using P2PDraw.App.Controls;
using P2PDraw.App.ViewModels;
using P2PDraw.App.Views;
using Xunit;

namespace P2PDraw.Tests.App;

public class EditorTests
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<P2PDraw.App.App>().UseHeadless(new AvaloniaHeadlessPlatformOptions());

    [Fact]
    public async Task FoundPeerCanBeSelectedAndConnectedFromWindow()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(EditorTests));
        await session.Dispatch(async () =>
        {
            string sessionId = Guid.NewGuid().ToString();
            await using var first = new MainWindowViewModel(["0", sessionId, "1"]);
            await using var second = new MainWindowViewModel(["0", sessionId, "1"]);
            var window = new MainWindow { DataContext = first };
            try
            {
                window.Show();
                await first.StartListeningAsync();
                await second.StartListeningAsync();
                await WaitUntilAsync(() => first.DiscoveredPeers.Length == 1 && second.DiscoveredPeers.Length == 1);
                var list = window.FindControl<ComboBox>("DiscoveredPeerList")!;
                list.SelectedIndex = 0;
                Assert.NotNull(first.SelectedPeer);
                var button = window.FindControl<Button>("ConnectDiscoveredButton")!;
                Assert.True(button.IsEnabled);
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await WaitUntilAsync(() => first.ConnectedPeers == 1 && second.ConnectedPeers == 1);
                first.BeginStroke(new(10, 20));
                first.AppendStroke(new(30, 40));
                await WaitUntilAsync(() => second.OperationCount == 3);
                Assert.False(Assert.Single(second.Strokes).IsComplete);
                Assert.Equal(2, Assert.Single(second.Strokes).Points.Length);
                first.EndStroke();
                await WaitUntilAsync(() => second.OperationCount == 4);
                Assert.True(Assert.Single(second.Strokes).IsComplete);
                Assert.Equal(Normalize(first), Normalize(second));
            }
            finally
            {
                await first.DisposeAsync();
                window.Close();
            }
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task MinimumWindowSizeKeepsNetworkButtonsInsidePanel()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(EditorTests));
        await session.Dispatch(async () =>
        {
            await using var model = new MainWindowViewModel();
            var window = new MainWindow { DataContext = model };
            window.Width = window.MinWidth;
            window.Height = window.MinHeight;
            window.Show();
            window.UpdateLayout();
            var disconnectButton = window.FindControl<Button>("DisconnectButton")!;
            var surface = window.FindControl<DrawingCanvas>("DrawingSurface")!;
            Point buttonBottom = disconnectButton.TranslatePoint(new Point(0, disconnectButton.Bounds.Height), window)!.Value;
            Point canvasBottom = surface.TranslatePoint(new Point(0, surface.Bounds.Height), window)!.Value;
            Assert.True(buttonBottom.Y <= canvasBottom.Y);
            await model.DisposeAsync();
            window.Close();
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task RemoteClearWhilePointerIsCapturedDoesNotResurrectStroke()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(EditorTests));
        await session.Dispatch(async () =>
        {
            string sessionId = Guid.NewGuid().ToString();
            var first = new MainWindowViewModel(["0", sessionId, "1"]);
            var second = new MainWindowViewModel(["0", sessionId, "1"]);
            var window = new MainWindow { DataContext = first };
            try
            {
                window.Show();
                window.UpdateLayout();
                await first.StartListeningAsync();
                await second.StartListeningAsync();
                first.RemotePort = second.LocalPort;
                await first.ConnectAsync();
                await WaitUntilAsync(() => first.ConnectedPeers == 1 && second.ConnectedPeers == 1);
                var surface = window.FindControl<DrawingCanvas>("DrawingSurface")!;
                Point start = surface.TranslatePoint(new Point(40, 50), window)!.Value;
                Point end = surface.TranslatePoint(new Point(100, 120), window)!.Value;
                window.MouseDown(start, MouseButton.Left);
                await WaitUntilAsync(() => second.OperationCount == 2);
                second.ClearCanvas();
                await WaitUntilAsync(() => first.OperationCount == 3);
                window.MouseMove(end, RawInputModifiers.LeftMouseButton);
                window.MouseUp(end, MouseButton.Left);
                Assert.Empty(first.Strokes);
                Assert.Empty(second.Strokes);
                Assert.Equal(Normalize(first), Normalize(second));

                DrawStroke(window, new Point(150, 160), new Point(200, 210));
                await WaitUntilAsync(() => second.OperationCount == 7);
                Assert.True(Assert.Single(second.Strokes).IsComplete);
            }
            finally
            {
                await first.DisposeAsync();
                await second.DisposeAsync();
                window.Close();
            }
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task TwoWindowsSyncPointerDrawingClearAndReconnect()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(EditorTests));
        await session.Dispatch(async () =>
        {
            string sessionId = Guid.NewGuid().ToString();
            var first = new MainWindowViewModel(["0", sessionId, "1"]);
            var second = new MainWindowViewModel(["0", sessionId, "1"]);
            var firstWindow = new MainWindow { DataContext = first };
            var secondWindow = new MainWindow { DataContext = second };
            try
            {
                firstWindow.Show();
                secondWindow.Show();
                firstWindow.UpdateLayout();
                secondWindow.UpdateLayout();
                firstWindow.FindControl<Button>("ListenButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                secondWindow.FindControl<Button>("ListenButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await WaitUntilAsync(() => first.CanConnect && second.CanConnect);
                first.RemotePort = second.LocalPort;
                var connectButton = firstWindow.FindControl<Button>("ConnectButton")!;
                connectButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await WaitUntilAsync(() => first.ConnectedPeers == 1 && second.ConnectedPeers == 1);

                firstWindow.FindControl<ComboBox>("BrushColors")!.SelectedIndex = 2;
                firstWindow.FindControl<Slider>("BrushSize")!.Value = 7;
                DrawStroke(firstWindow, new Point(40, 50), new Point(100, 120));
                await WaitUntilAsync(() => second.OperationCount == 4);
                var coloredStroke = Assert.Single(second.Strokes);
                Assert.Equal(0xFFDC2626u, coloredStroke.Color);
                Assert.Equal(7, coloredStroke.Width);
                Assert.True(coloredStroke.IsComplete);
                Assert.Equal(Normalize(first), Normalize(second));

                DrawStroke(secondWindow, new Point(160, 70), new Point(220, 140));
                await WaitUntilAsync(() => first.OperationCount == 8 && second.OperationCount == 8);
                var clearButton = firstWindow.FindControl<Button>("ClearButton")!;
                clearButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await WaitUntilAsync(() => second.OperationCount == 9);
                Assert.Empty(second.Strokes);

                firstWindow.FindControl<Button>("DisconnectButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await WaitUntilAsync(() => first.ConnectedPeers == 0 && second.ConnectedPeers == 0);
                DrawStroke(firstWindow, new Point(50, 180), new Point(110, 220));
                DrawStroke(secondWindow, new Point(170, 180), new Point(230, 220));
                clearButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                connectButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await WaitUntilAsync(() => first.OperationCount == 18 && second.OperationCount == 18);
                Assert.Equal(Normalize(first), Normalize(second));
                Assert.Single(first.Strokes);
                Assert.Equal(3, first.GetSnapshot().Tombstones.Length);
                var surface = firstWindow.FindControl<DrawingCanvas>("DrawingSurface")!;
                Assert.Equal(first.Strokes, surface.Strokes);
            }
            finally
            {
                await first.DisposeAsync();
                await second.DisposeAsync();
                firstWindow.Close();
                secondWindow.Close();
            }
            return true;
        }, TestContext.Current.CancellationToken);
    }

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

    [Theory]
    [InlineData("invalid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task InvalidSessionDoesNotCreateOperationsOrListener(string sessionId)
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(EditorTests));
        await session.Dispatch(async () =>
        {
            await using var model = new MainWindowViewModel(["0", sessionId, "1"]);
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

    private static void DrawStroke(MainWindow window, Point start, Point end)
    {
        var surface = window.FindControl<DrawingCanvas>("DrawingSurface")!;
        Point windowStart = surface.TranslatePoint(start, window)!.Value;
        Point windowEnd = surface.TranslatePoint(end, window)!.Value;
        window.MouseDown(windowStart, MouseButton.Left);
        window.MouseMove(windowEnd, RawInputModifiers.LeftMouseButton);
        window.MouseUp(windowEnd, MouseButton.Left);
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
