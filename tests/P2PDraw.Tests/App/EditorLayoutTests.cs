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
    public async Task SessionDrawerClosesWithoutReducingCanvas(int width, int height)
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
            var panel = view.FindControl<Border>("NetworkPanel")!;
            var sessionButton = view.FindControl<Button>("SessionButton")!;
            Assert.False(panel.IsVisible);
            Assert.True(canvas.Bounds.Width >= width - 40);
            var originalBounds = canvas.Bounds;

            sessionButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.UpdateLayout();
            Assert.True(panel.IsVisible);
            Assert.False(view.FindControl<LayersPanel>("LayerSide")!.IsVisible);
            Assert.True(panel.Bounds.Width <= view.Bounds.Width);
            Assert.True(panel.Bounds.Height <= canvas.Bounds.Height + 2);
            sessionButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.UpdateLayout();
            Assert.False(panel.IsVisible);
            Assert.Equal(originalBounds, canvas.Bounds);
            canvas.ZoomAt(2, new(50, 50));
            Assert.Contains("200", view.FindControl<Button>("ResetZoomButton")!.Content!.ToString());
            view.FindControl<Button>("ResetZoomButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(1, canvas.Zoom);
            Assert.Equal(0, model.OperationCount);
            window.Close();
            return true;
        }, TestContext.Current.CancellationToken);
    }
}
