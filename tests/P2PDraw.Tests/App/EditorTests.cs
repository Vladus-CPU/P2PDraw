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

[Collection("Avalonia")]
public partial class EditorTests
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<P2PDraw.App.App>().UseHeadless(new AvaloniaHeadlessPlatformOptions());

    [Fact]
    public async Task CompactTouchEditorSynchronizesWithDesktopAfterOfflineClear()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(EditorTests));
        await session.Dispatch(async () =>
        {
            string sessionId = Guid.NewGuid().ToString();
            await using var mobile = new MainWindowViewModel(["0", sessionId, "1"]);
            await using var desktop = new MainWindowViewModel(["0", sessionId, "1"]);
            var mobileView = new MainView { DataContext = mobile };
            var mobileWindow = new Window { Content = mobileView, Width = 390, Height = 844 };
            var desktopWindow = new MainWindow { DataContext = desktop };
            try
            {
                mobileWindow.Show();
                desktopWindow.Show();
                mobileWindow.UpdateLayout();
                desktopWindow.UpdateLayout();
                await mobile.StartListeningAsync();
                await desktop.StartListeningAsync();
                mobile.RemotePort = desktop.LocalPort;
                await mobile.ConnectAsync();
                await WaitUntilAsync(() => mobile.ConnectedPeers == 1 && desktop.ConnectedPeers == 1);

                var surface = mobileView.FindControl<DrawingCanvas>("DrawingSurface")!;
                Point start = surface.TranslatePoint(new Point(20, 30), mobileWindow)!.Value;
                Point middle = surface.TranslatePoint(new Point(50, 60), mobileWindow)!.Value;
                Point end = surface.TranslatePoint(new Point(80, 90), mobileWindow)!.Value;
                using var finger = mobileWindow.TouchBegin(start);
                mobileWindow.TouchMove(finger, middle);
                await WaitUntilAsync(() => desktop.OperationCount == 3);
                Assert.False(Assert.Single(desktop.Strokes).IsComplete);
                DrawStroke(desktopWindow, new Point(150, 160), new Point(200, 210));
                mobileWindow.TouchEnd(finger, end);
                await WaitUntilAsync(() => mobile.OperationCount == 9 && desktop.OperationCount == 9);
                Assert.Equal(Normalize(mobile), Normalize(desktop));

                mobile.Disconnect();
                await WaitUntilAsync(() => mobile.ConnectedPeers == 0 && desktop.ConnectedPeers == 0);
                desktop.ClearCanvas();
                using var offlineFinger = mobileWindow.TouchBegin(start);
                mobileWindow.TouchEnd(offlineFinger, end);
                Assert.Equal(0, mobile.ConnectedPeers);
                Assert.Equal(0, desktop.ConnectedPeers);
                Assert.Equal(13, mobile.OperationCount);
                Assert.Equal(10, desktop.OperationCount);
                await mobile.ConnectAsync();
                await WaitUntilAsync(() => mobile.OperationCount == 14 && desktop.OperationCount == 14);
                Assert.Equal(Normalize(mobile), Normalize(desktop));
                Assert.Single(mobile.Strokes);
                Assert.Equal(2, mobile.GetSnapshot().Tombstones.Length);
            }
            finally
            {
                await mobile.DisposeAsync();
                await desktop.DisposeAsync();
                mobileWindow.Close();
                desktopWindow.Close();
            }
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task MobileViewRecreationRetainsDocumentBrushAndSession()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(EditorTests));
        await session.Dispatch(async () =>
        {
            var app = (P2PDraw.App.App)Application.Current!;
            try
            {
                var firstView = app.CreateMobileView();
                var model = (MainWindowViewModel)firstView.DataContext!;
                firstView.FindControl<ComboBox>("BrushColors")!.SelectedIndex = 2;
                model.BrushWidth = 7;
                model.BeginStroke(new(10, 20));
                model.EndStroke();

                var recreatedView = app.CreateMobileView();
                Assert.NotSame(firstView, recreatedView);
                Assert.Same(model, recreatedView.DataContext);
                Assert.Single(model.Strokes);
                Assert.True(model.Strokes[0].IsComplete);
                Assert.Equal(7, model.BrushWidth);
                var color = Assert.IsType<ComboBoxItem>(recreatedView.FindControl<ComboBox>("BrushColors")!.SelectedItem);
                Assert.Equal("#FFDC2626", color.Tag);
                Assert.Equal(model.SessionId, app.MobileModel!.SessionId);

                await app.CloseMobileEditorAsync();
                Assert.Null(app.MobileModel);
                var freshView = app.CreateMobileView();
                Assert.NotSame(model, freshView.DataContext);
                Assert.Empty(app.MobileModel!.Strokes);
            }
            finally
            {
                await app.CloseMobileEditorAsync();
            }
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ResumePreservesOfflineEditsAndRespectsManualDisconnect()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(EditorTests));
        await session.Dispatch(async () =>
        {
            string sessionId = Guid.NewGuid().ToString();
            await using var first = new MainWindowViewModel(["0", sessionId, "1"]);
            await using var second = new MainWindowViewModel(["0", sessionId, "1"]);
            await first.StartListeningAsync();
            await second.StartListeningAsync();
            first.RemotePort = second.LocalPort;
            await first.ConnectAsync();
            first.BeginStroke(new(1, 2));
            first.EndStroke();
            await WaitUntilAsync(() => second.OperationCount == 3);
            second.Disconnect();
            await WaitUntilAsync(() => first.ConnectedPeers == 0 && second.ConnectedPeers == 0);

            first.ResumeNetworking();
            second.ResumeNetworking();
            first.BeginStroke(new(3, 4));
            first.EndStroke();
            second.ClearCanvas();
            second.BeginStroke(new(5, 6));
            second.EndStroke();
            Assert.Equal(0, first.ConnectedPeers);
            Assert.Equal(0, second.ConnectedPeers);
            Assert.Equal(6, first.OperationCount);
            Assert.Equal(7, second.OperationCount);

            second.RemotePort = first.LocalPort;
            await second.ConnectAsync();
            await WaitUntilAsync(() => first.OperationCount == 10 && second.OperationCount == 10);
            Assert.Equal(Normalize(first), Normalize(second));
            Assert.Equal(2, first.Strokes.Length);
            Assert.Single(first.GetSnapshot().Tombstones);
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(360, 640)]
    [InlineData(390, 844)]
    [InlineData(844, 390)]
    public async Task CompactViewKeepsCanvasAndConnectionSettingsUsable(int width, int height)
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(EditorTests));
        await session.Dispatch(async () =>
        {
            await using var model = new MainWindowViewModel();
            var view = new MainView { DataContext = model };
            var window = new Window { Content = view, Width = width, Height = height };
            var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            view.Loaded += (_, _) => loaded.TrySetResult();
            window.Show();
            window.UpdateLayout();
            await loaded.Task.WaitAsync(TestContext.Current.CancellationToken);
            var canvas = view.FindControl<DrawingCanvas>("DrawingSurface")!;
            Assert.False(view.FindControl<Border>("NetworkPanel")!.IsVisible);
            Assert.True(canvas.Bounds.Width >= width - 40);
            Assert.True(canvas.Bounds.Height > height * 0.35,
                $"Canvas: {canvas.Bounds}; header: {view.FindControl<Grid>("HeaderGrid")!.Bounds}; tools: {view.FindControl<Border>("BrushPanel")!.Bounds}; footer: {view.FindControl<Grid>("FooterGrid")!.Bounds}; window: {window.Bounds}");

            view.FindControl<Button>("FilesButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.UpdateLayout();
            var clear = view.FindControl<Button>("ClearButton")!;
            clear.BringIntoView();
            window.UpdateLayout();
            Point clearPosition = clear.TranslatePoint(new Point(clear.Bounds.Width, clear.Bounds.Height), window)!.Value;
            Assert.InRange(clearPosition.X, 0, window.Bounds.Width);
            Assert.InRange(clearPosition.Y, 0, window.Bounds.Height);

            view.FindControl<Button>("SessionButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.UpdateLayout();
            var panel = view.FindControl<Border>("SidePanel")!;
            var networkScroll = view.FindControl<ScrollViewer>("NetworkScroll")!;
            Assert.True(networkScroll.Bounds.Height > 0);
            Assert.True(networkScroll.Bounds.Height <= panel.Bounds.Height);
            Assert.NotNull(view.FindControl<TextBox>("SessionIdBox"));
            Assert.NotNull(view.FindControl<TextBox>("RemoteAddressBox"));
            window.Close();
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task TouchDrawingIgnoresSecondFingerAndCompletesStroke()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(EditorTests));
        await session.Dispatch(async () =>
        {
            await using var model = new MainWindowViewModel();
            var view = new MainView { DataContext = model };
            var window = new Window { Content = view, Width = 390, Height = 844 };
            window.Show();
            window.UpdateLayout();
            var surface = view.FindControl<DrawingCanvas>("DrawingSurface")!;
            Point start = surface.TranslatePoint(new Point(20, 30), window)!.Value;
            Point middle = surface.TranslatePoint(new Point(70, 90), window)!.Value;
            Point end = surface.TranslatePoint(new Point(100, 120), window)!.Value;
            using var firstFinger = window.TouchBegin(start);
            window.TouchMove(firstFinger, middle);
            using var secondFinger = window.TouchBegin(end);
            window.TouchEnd(secondFinger, end);
            Assert.False(Assert.Single(model.Strokes).IsComplete);
            window.TouchEnd(firstFinger, end);

            var stroke = Assert.Single(model.Strokes);
            Assert.True(stroke.IsComplete);
            Assert.Equal(3, stroke.Points.Length);
            Assert.Equal(5, model.OperationCount);
            window.Close();
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task UnavailableDiscoveryDoesNotBlockManualTcpConnection()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(EditorTests));
        await session.Dispatch(async () =>
        {
            using var occupiedPort = new UdpClient(AddressFamily.InterNetwork);
            occupiedPort.ExclusiveAddressUse = true;
            occupiedPort.Client.Bind(new IPEndPoint(IPAddress.Any, P2PDraw.Networking.Discovery.PeerDiscovery.DefaultPort));
            string sessionId = Guid.NewGuid().ToString();
            await using var first = new MainWindowViewModel(["0", sessionId, "1"]);
            await using var second = new MainWindowViewModel(["0", sessionId, "1"]);

            await first.StartListeningAsync();
            await second.StartListeningAsync();
            Assert.True(first.CanConnect);
            Assert.Contains("Пошук недоступний", first.DiscoveryStatus);
            Assert.Empty(first.DiscoveredPeers);
            first.RemotePort = second.LocalPort;
            await first.ConnectAsync();
            await WaitUntilAsync(() => first.ConnectedPeers == 1 && second.ConnectedPeers == 1);
            first.BeginStroke(new(10, 20));
            first.EndStroke();
            await WaitUntilAsync(() => second.OperationCount == 3);
            Assert.Equal(Normalize(first), Normalize(second));
            return true;
        }, TestContext.Current.CancellationToken);
    }

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
                var list = window.Editor.FindControl<ComboBox>("DiscoveredPeerList")!;
                list.SelectedIndex = 0;
                Assert.NotNull(first.SelectedPeer);
                var button = window.Editor.FindControl<Button>("ConnectDiscoveredButton")!;
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
            window.Editor.FindControl<Button>("SessionButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.UpdateLayout();
            var disconnectButton = window.Editor.FindControl<Button>("DisconnectButton")!;
            var surface = window.Editor.FindControl<DrawingCanvas>("DrawingSurface")!;
            disconnectButton.BringIntoView();
            window.UpdateLayout();
            Point buttonTop = disconnectButton.TranslatePoint(default, window)!.Value;
            Point buttonBottom = disconnectButton.TranslatePoint(new Point(0, disconnectButton.Bounds.Height), window)!.Value;
            Point canvasBottom = surface.TranslatePoint(new Point(0, surface.Bounds.Height), window)!.Value;
            Assert.True(buttonTop.Y >= surface.TranslatePoint(default, window)!.Value.Y);
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
                var surface = window.Editor.FindControl<DrawingCanvas>("DrawingSurface")!;
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
                firstWindow.Editor.FindControl<Button>("ListenButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                secondWindow.Editor.FindControl<Button>("ListenButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await WaitUntilAsync(() => first.CanConnect && second.CanConnect);
                first.RemotePort = second.LocalPort;
                var connectButton = firstWindow.Editor.FindControl<Button>("ConnectButton")!;
                connectButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await WaitUntilAsync(() => first.ConnectedPeers == 1 && second.ConnectedPeers == 1);

                firstWindow.Editor.FindControl<ComboBox>("BrushColors")!.SelectedIndex = 2;
                firstWindow.Editor.FindControl<Slider>("BrushSize")!.Value = 7;
                DrawStroke(firstWindow, new Point(40, 50), new Point(100, 120));
                await WaitUntilAsync(() => second.OperationCount == 4);
                var coloredStroke = Assert.Single(second.Strokes);
                Assert.Equal(0xFFDC2626u, coloredStroke.Color);
                Assert.Equal(7, coloredStroke.Width);
                Assert.True(coloredStroke.IsComplete);
                Assert.Equal(Normalize(first), Normalize(second));

                DrawStroke(secondWindow, new Point(160, 70), new Point(220, 140));
                await WaitUntilAsync(() => first.OperationCount == 8 && second.OperationCount == 8);
                var clearButton = firstWindow.Editor.FindControl<Button>("ClearButton")!;
                clearButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                firstWindow.Editor.FindControl<Button>("ConfirmActionButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await WaitUntilAsync(() => second.OperationCount == 9);
                Assert.Empty(second.Strokes);

                firstWindow.Editor.FindControl<Button>("DisconnectButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await WaitUntilAsync(() => first.ConnectedPeers == 0 && second.ConnectedPeers == 0);
                DrawStroke(firstWindow, new Point(50, 180), new Point(110, 220));
                DrawStroke(secondWindow, new Point(170, 180), new Point(230, 220));
                clearButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                firstWindow.Editor.FindControl<Button>("ConfirmActionButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                connectButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await WaitUntilAsync(() => first.OperationCount == 18 && second.OperationCount == 18);
                Assert.Equal(Normalize(first), Normalize(second));
                Assert.Single(first.Strokes);
                Assert.Equal(3, first.GetSnapshot().Tombstones.Length);
                var surface = firstWindow.Editor.FindControl<DrawingCanvas>("DrawingSurface")!;
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
        return JsonSerializer.Serialize(new
        {
            snapshot.Strokes,
            snapshot.Shapes,
            snapshot.Layers,
            snapshot.LayerTombstones,
            snapshot.Tombstones,
            snapshot.PendingStrokeIds,
            snapshot.PendingLayerIds,
            snapshot.Operations
        });
    }

    private static void DrawStroke(MainWindow window, Point start, Point end)
    {
        var surface = window.Editor.FindControl<DrawingCanvas>("DrawingSurface")!;
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
