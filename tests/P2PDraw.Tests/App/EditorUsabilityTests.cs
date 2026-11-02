using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using P2PDraw.App.Controls;
using P2PDraw.App.ViewModels;
using P2PDraw.App.Views;
using Xunit;

namespace P2PDraw.Tests.App;

public partial class EditorTests
{
    [Theory]
    [InlineData(360, 640)]
    [InlineData(412, 915)]
    [InlineData(915, 412)]
    [InlineData(844, 390)]
    public async Task MobileControlsStayInsideWindowAndZoomDoesNotChangeDocument(int width, int height)
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
            var anchor = canvas.ScreenToDocument(new Point(canvas.Bounds.Width / 2, canvas.Bounds.Height / 2));
            view.FindControl<Button>("ZoomInButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(1.2, canvas.Zoom, 8);
            var restored = canvas.ScreenToDocument(new Point(canvas.Bounds.Width / 2, canvas.Bounds.Height / 2));
            Assert.Equal(anchor.X, restored.X, 8);
            Assert.Equal(anchor.Y, restored.Y, 8);
            view.FindControl<Button>("ZoomOutButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(1, canvas.Zoom, 8);
            Assert.Equal(0, model.OperationCount);

            foreach (string name in new[] { "FilesButton", "LayersButton", "ZoomInButton", "ZoomOutButton", "ResetZoomButton" })
            {
                var button = view.FindControl<Button>(name)!;
                Assert.True(button.IsEffectivelyVisible);
                Assert.True(button.Bounds.Width >= 48);
                Assert.True(button.Bounds.Height >= 48);
                Point corner = button.TranslatePoint(new Point(button.Bounds.Width, button.Bounds.Height), window)!.Value;
                Assert.InRange(corner.X, 0, window.Bounds.Width);
                Assert.InRange(corner.Y, 0, window.Bounds.Height);
            }
            var before = canvas.Bounds.Size;
            view.FindControl<Button>("FilesButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.UpdateLayout();
            var files = view.FindControl<Border>("FilesPanel")!;
            Assert.True(files.IsVisible);
            Assert.Equal(before, canvas.Bounds.Size);
            Assert.Equal(window.StorageProvider.CanSave, view.FindControl<Button>("ExportSvgButton")!.IsEnabled);
            Assert.Equal(window.StorageProvider.CanOpen, view.FindControl<Button>("OpenProjectButton")!.IsEnabled);
            Point bottom = files.TranslatePoint(new Point(files.Bounds.Width, files.Bounds.Height), window)!.Value;
            Assert.InRange(bottom.X, 0, window.Bounds.Width);
            Assert.InRange(bottom.Y, 0, window.Bounds.Height);
            window.Close();
            return true;
        }, TestContext.Current.CancellationToken);
    }
}
