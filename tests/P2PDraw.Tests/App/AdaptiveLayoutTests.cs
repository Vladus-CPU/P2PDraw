using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using P2PDraw.App.Controls;
using P2PDraw.App.ViewModels;
using P2PDraw.App.Views;
using Xunit;

namespace P2PDraw.Tests.App;

public partial class EditorTests
{
    [Theory]
    [InlineData(320, 640)]
    [InlineData(360, 740)]
    [InlineData(393, 851)]
    [InlineData(412, 915)]
    [InlineData(640, 360)]
    [InlineData(844, 390)]
    [InlineData(800, 1280)]
    [InlineData(1280, 800)]
    [InlineData(1024, 768)]
    [InlineData(1366, 768)]
    [InlineData(1920, 1080)]
    public async Task AdaptiveEditorKeepsCanvasAndMainActionsAvailable(int width, int height)
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
            view.TryClosePanel();
            window.UpdateLayout();

            var canvas = view.FindControl<DrawingCanvas>("DrawingSurface")!;
            Assert.True(canvas.Bounds.Width >= width - 40, $"Canvas width: {canvas.Bounds.Width}; window width: {width}");
            Assert.True(canvas.Bounds.Height > height / 2.0, $"Canvas height: {canvas.Bounds.Height}; window height: {height}");
            Point canvasTop = canvas.TranslatePoint(default, window)!.Value;
            Point canvasBottom = canvas.TranslatePoint(new Point(canvas.Bounds.Width, canvas.Bounds.Height), window)!.Value;
            var toolbar = view.FindControl<Border>("BrushPanel")!;
            Point toolbarBottom = toolbar.TranslatePoint(new Point(0, toolbar.Bounds.Height), window)!.Value;
            var footer = view.FindControl<Grid>("FooterGrid")!;
            Point footerTop = footer.TranslatePoint(default, window)!.Value;
            Assert.True(toolbarBottom.Y <= canvasTop.Y);
            Assert.True(canvasBottom.Y <= footerTop.Y);

            foreach (string name in new[] { "FilesButton", "LayersButton", "SessionButton", "SelectTool", "BrushTool", "LineTool", "RectangleTool", "EllipseTool", "ZoomInButton", "ZoomOutButton", "ResetZoomButton" })
            {
                var button = view.FindControl<Button>(name)!;
                Assert.True(button.IsEffectivelyVisible, name);
                button.BringIntoView();
                window.UpdateLayout();
                Assert.True(button.Bounds.Width >= 48, $"{name}: {button.Bounds}");
                Assert.True(button.Bounds.Height >= 48, $"{name}: {button.Bounds}");
                Point top = button.TranslatePoint(default, window)!.Value;
                Point bottom = button.TranslatePoint(new Point(button.Bounds.Width, button.Bounds.Height), window)!.Value;
                Assert.InRange(top.X, 0, window.Bounds.Width);
                Assert.InRange(top.Y, 0, window.Bounds.Height);
                Assert.True(bottom.X <= window.Bounds.Width, $"{name} right edge: {bottom.X}; window width: {window.Bounds.Width}");
                Assert.InRange(bottom.Y, 0, window.Bounds.Height);
            }
            Assert.Equal(0, model.OperationCount);
            window.Close();
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(393, 851, false)]
    [InlineData(844, 390, false)]
    [InlineData(1024, 768, true)]
    [InlineData(1366, 768, true)]
    public async Task SecondaryPanelsSwitchExclusivelyAndCloseToCanvas(int width, int height, bool docked)
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
            view.TryClosePanel();
            window.UpdateLayout();
            var canvas = view.FindControl<DrawingCanvas>("DrawingSurface")!;
            var originalSize = canvas.Bounds.Size;
            var layers = view.FindControl<LayersPanel>("LayerSide")!;
            var network = view.FindControl<Border>("NetworkPanel")!;
            var files = view.FindControl<Border>("FilesPanel")!;
            var panel = view.FindControl<Border>("SidePanel")!;

            view.FindControl<Button>("LayersButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.UpdateLayout();
            Assert.True(layers.IsVisible);
            Assert.False(network.IsVisible);
            Assert.False(files.IsVisible);
            if (docked)
            {
                Assert.True(canvas.Bounds.Width < originalSize.Width);
                Point canvasRight = canvas.TranslatePoint(new Point(canvas.Bounds.Width, 0), window)!.Value;
                Point panelLeft = panel.TranslatePoint(default, window)!.Value;
                Assert.True(canvasRight.X <= panelLeft.X);
            }
            else
            {
                Assert.Equal(originalSize, canvas.Bounds.Size);
            }

            view.FindControl<Button>("SessionButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.UpdateLayout();
            Assert.False(layers.IsVisible);
            Assert.True(network.IsVisible);
            Assert.False(files.IsVisible);
            view.FindControl<Button>("FilesButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.UpdateLayout();
            Assert.False(layers.IsVisible);
            Assert.False(network.IsVisible);
            Assert.True(files.IsVisible);
            Point panelBottom = panel.TranslatePoint(new Point(panel.Bounds.Width, panel.Bounds.Height), window)!.Value;
            Assert.InRange(panelBottom.X, 0, window.Bounds.Width);
            Assert.InRange(panelBottom.Y, 0, window.Bounds.Height);

            view.FindControl<Button>("ClosePanelButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.UpdateLayout();
            Assert.False(panel.IsVisible);
            Assert.Equal(originalSize, canvas.Bounds.Size);
            Assert.Equal(0, model.OperationCount);
            window.Close();
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2)]
    public async Task DpiChangePreservesLogicalControlsAndCanvasInput(double scaling)
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(EditorTests));
        await session.Dispatch(async () =>
        {
            await using var model = new MainWindowViewModel();
            var view = new MainView { DataContext = model };
            var window = new Window { Content = view, Width = 800, Height = 600 };
            var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            view.Loaded += (_, _) => loaded.TrySetResult();
            window.Show();
            window.UpdateLayout();
            await loaded.Task.WaitAsync(TestContext.Current.CancellationToken);
            window.SetRenderScaling(scaling);
            window.UpdateLayout();
            Assert.Equal(scaling, window.RenderScaling);
            var canvas = view.FindControl<DrawingCanvas>("DrawingSurface")!;
            Assert.True(canvas.Bounds.Width > 700);
            Assert.True(canvas.Bounds.Height > 300);
            var brush = view.FindControl<Button>("BrushTool")!;
            Assert.True(brush.Bounds.Width >= 48);
            Assert.True(brush.Bounds.Height >= 48);
            Point start = canvas.TranslatePoint(new Point(20, 30), window)!.Value;
            Point end = canvas.TranslatePoint(new Point(80, 90), window)!.Value;

            using var finger = window.TouchBegin(start);
            window.TouchMove(finger, end);
            window.TouchEnd(finger, end);

            var stroke = Assert.Single(model.Strokes);
            Assert.True(stroke.IsComplete);
            Assert.Equal(20, stroke.Points[0].X, 8);
            Assert.Equal(30, stroke.Points[0].Y, 8);
            Assert.Equal(80, stroke.Points[^1].X, 8);
            Assert.Equal(90, stroke.Points[^1].Y, 8);
            window.Close();
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ResizingAcrossLayoutsKeepsViewAndDocumentState()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(EditorTests));
        await session.Dispatch(async () =>
        {
            await using var model = new MainWindowViewModel();
            model.BeginStroke(new(10, 20));
            model.AppendStroke(new(30, 40));
            model.EndStroke();
            string before = Normalize(model);
            var view = new MainView { DataContext = model };
            var window = new Window { Content = view, Width = 1280, Height = 800 };
            var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            view.Loaded += (_, _) => loaded.TrySetResult();
            window.Show();
            window.UpdateLayout();
            await loaded.Task.WaitAsync(TestContext.Current.CancellationToken);
            var canvas = view.FindControl<DrawingCanvas>("DrawingSurface")!;
            canvas.ZoomAt(2, new Point(40, 50));
            canvas.PanBy(new Vector(25, 35));
            Vector pan = canvas.Pan;

            foreach (var size in new[] { new Size(360, 740), new Size(844, 390), new Size(1280, 800) })
            {
                window.Width = size.Width;
                window.Height = size.Height;
                window.UpdateLayout();
                view.TryClosePanel();
                window.UpdateLayout();
                Assert.Equal(2, canvas.Zoom);
                Assert.Equal(pan, canvas.Pan);
                Assert.Equal(before, Normalize(model));
                Assert.True(canvas.Bounds.Height > size.Height / 2);
            }
            window.Close();
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(320, 640)]
    [InlineData(844, 390)]
    public async Task IncreasedTextKeepsConfirmationTextAndActionsInsideWindow(int width, int height)
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(EditorTests));
        await session.Dispatch(async () =>
        {
            await using var model = new MainWindowViewModel();
            var view = new MainView { DataContext = model, FontSize = 20 };
            var window = new Window { Content = view, Width = width, Height = height };
            var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            view.Loaded += (_, _) => loaded.TrySetResult();
            window.Show();
            window.UpdateLayout();
            await loaded.Task.WaitAsync(TestContext.Current.CancellationToken);
            view.FindControl<Button>("ClearButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.UpdateLayout();
            Assert.True(view.FindControl<Border>("ConfirmationOverlay")!.IsVisible);
            foreach (string name in new[] { "CancelActionButton", "ConfirmActionButton" })
            {
                var button = view.FindControl<Button>(name)!;
                Assert.Equal(HorizontalAlignment.Center, button.HorizontalContentAlignment);
                Assert.Equal(VerticalAlignment.Center, button.VerticalContentAlignment);
                var text = button.GetVisualDescendants().OfType<TextBlock>().First();
                Point textCenter = text.TranslatePoint(new Point(text.Bounds.Width / 2, text.Bounds.Height / 2), button)!.Value;
                Assert.Equal(button.Bounds.Width / 2, textCenter.X, 1);
                Assert.Equal(button.Bounds.Height / 2, textCenter.Y, 1);
                Point top = button.TranslatePoint(default, window)!.Value;
                Point bottom = button.TranslatePoint(new Point(button.Bounds.Width, button.Bounds.Height), window)!.Value;
                Assert.InRange(top.X, 0, window.Bounds.Width);
                Assert.InRange(top.Y, 0, window.Bounds.Height);
                Assert.InRange(bottom.X, 0, window.Bounds.Width);
                Assert.InRange(bottom.Y, 0, window.Bounds.Height);
                Assert.True(text.Bounds.Width < button.Bounds.Width);
            }
            view.FindControl<Button>("CancelActionButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(0, model.OperationCount);
            window.Close();
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(393, 500)]
    [InlineData(844, 280)]
    public async Task ManualConnectionFieldsCanScrollIntoReducedViewport(int width, int height)
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(EditorTests));
        await session.Dispatch(async () =>
        {
            await using var model = new MainWindowViewModel();
            var view = new MainView { DataContext = model };
            var window = new Window { Content = view, Width = width, Height = 740 };
            var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            view.Loaded += (_, _) => loaded.TrySetResult();
            window.Show();
            window.UpdateLayout();
            await loaded.Task.WaitAsync(TestContext.Current.CancellationToken);
            view.FindControl<Button>("SessionButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            view.FindControl<Expander>("ManualConnectionExpander")!.IsExpanded = true;
            window.Height = height;
            window.UpdateLayout();
            var scroll = view.FindControl<ScrollViewer>("NetworkScroll")!;
            var field = view.FindControl<TextBox>("LocalPortBox")!;
            Assert.True(field.Focus());
            field.BringIntoView();
            window.UpdateLayout();

            Assert.True(scroll.Extent.Height > scroll.Viewport.Height);
            Assert.True(scroll.Offset.Y > 0);
            Point top = field.TranslatePoint(default, scroll)!.Value;
            Point bottom = field.TranslatePoint(new Point(field.Bounds.Width, field.Bounds.Height), scroll)!.Value;
            Assert.InRange(top.Y, 0, scroll.Bounds.Height);
            Assert.InRange(bottom.Y, 0, scroll.Bounds.Height);
            Assert.Equal(0, model.OperationCount);
            window.Height = 740;
            window.UpdateLayout();
            Assert.True(view.FindControl<DrawingCanvas>("DrawingSurface")!.Bounds.Height > 370);
            window.Close();
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(393, 500)]
    [InlineData(844, 280)]
    public async Task FocusedConnectionFieldRemainsVisibleWhenViewportShrinks(int width, int height)
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(EditorTests));
        await session.Dispatch(async () =>
        {
            await using var model = new MainWindowViewModel();
            var view = new MainView { DataContext = model };
            var window = new Window { Content = view, Width = width, Height = 740 };
            var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            view.Loaded += (_, _) => loaded.TrySetResult();
            window.Show();
            window.UpdateLayout();
            await loaded.Task.WaitAsync(TestContext.Current.CancellationToken);
            view.FindControl<Button>("SessionButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            view.FindControl<Expander>("ManualConnectionExpander")!.IsExpanded = true;
            window.UpdateLayout();
            var field = view.FindControl<TextBox>("LocalPortBox")!;
            Assert.True(field.Focus());
            field.BringIntoView();
            window.UpdateLayout();

            window.Height = height;
            window.UpdateLayout();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
            window.UpdateLayout();

            var scroll = view.FindControl<ScrollViewer>("NetworkScroll")!;
            Assert.True(field.IsFocused);
            Point top = field.TranslatePoint(default, scroll)!.Value;
            Point bottom = field.TranslatePoint(new Point(field.Bounds.Width, field.Bounds.Height), scroll)!.Value;
            Assert.InRange(top.Y, 0, scroll.Bounds.Height);
            Assert.InRange(bottom.Y, 0, scroll.Bounds.Height);
            Assert.Equal(0, model.OperationCount);
            window.Close();
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ActiveToolRemainsVisibleAfterLayoutChanges()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(EditorTests));
        await session.Dispatch(async () =>
        {
            await using var model = new MainWindowViewModel();
            var view = new MainView { DataContext = model };
            var window = new Window { Content = view, Width = 1366, Height = 768 };
            var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            view.Loaded += (_, _) => loaded.TrySetResult();
            window.Show();
            window.UpdateLayout();
            await loaded.Task.WaitAsync(TestContext.Current.CancellationToken);
            var rectangle = view.FindControl<Button>("RectangleTool")!;

            rectangle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.Width = 320;
            window.Height = 640;
            window.UpdateLayout();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
            window.UpdateLayout();

            Assert.Equal(DrawingTool.Rectangle, view.FindControl<DrawingCanvas>("DrawingSurface")!.Tool);
            Assert.Contains("selected", rectangle.Classes);
            var scroll = view.FindControl<ScrollViewer>("DrawingToolsScroll")!;
            Point top = rectangle.TranslatePoint(default, scroll)!.Value;
            Point bottom = rectangle.TranslatePoint(new Point(rectangle.Bounds.Width, rectangle.Bounds.Height), scroll)!.Value;
            Assert.InRange(top.X, 0, scroll.Viewport.Width);
            Assert.InRange(bottom.X, 0, scroll.Viewport.Width);
            Assert.DoesNotContain("selected", view.FindControl<Button>("BrushTool")!.Classes);
            Assert.False(view.FindControl<Button>("DeleteButton")!.IsEnabled);
            Assert.False(view.FindControl<Button>("DuplicateButton")!.IsEnabled);
            Assert.Equal(0, model.OperationCount);
            window.Close();
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task LiveTextSizeChangeMovesPropertiesWithoutChangingDocument()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(EditorTests));
        await session.Dispatch(async () =>
        {
            await using var model = new MainWindowViewModel();
            var view = new MainView { DataContext = model, FontSize = 14 };
            var window = new Window { Content = view, Width = 1280, Height = 800 };
            var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            view.Loaded += (_, _) => loaded.TrySetResult();
            window.Show();
            window.UpdateLayout();
            await loaded.Task.WaitAsync(TestContext.Current.CancellationToken);
            var colors = view.FindControl<ComboBox>("BrushColors")!;
            var propertiesButton = view.FindControl<Button>("PropertiesButton")!;
            Assert.True(colors.IsEffectivelyVisible);
            Assert.False(propertiesButton.IsVisible);

            view.FontSize = 20;
            window.UpdateLayout();
            Assert.False(colors.IsEffectivelyVisible && TopLevel.GetTopLevel(colors) == window);
            Assert.True(propertiesButton.IsVisible);
            propertiesButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.UpdateLayout();
            Assert.True(colors.IsEffectivelyVisible);
            Assert.Same(window, TopLevel.GetTopLevel(colors));
            var panel = view.FindControl<Border>("SidePanel")!;
            Point colorsBottom = colors.TranslatePoint(new Point(colors.Bounds.Width, colors.Bounds.Height), panel)!.Value;
            Assert.InRange(colorsBottom.X, 0, panel.Bounds.Width);
            Assert.InRange(colorsBottom.Y, 0, panel.Bounds.Height);

            view.FontSize = 14;
            window.UpdateLayout();
            Assert.False(panel.IsVisible);
            Assert.False(propertiesButton.IsVisible);
            Assert.True(colors.IsEffectivelyVisible);
            Assert.Same(window, TopLevel.GetTopLevel(colors));
            Assert.Equal(0, model.OperationCount);
            window.Close();
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task PropertiesShowOnlySettingsUsedByCurrentTool()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(EditorTests));
        await session.Dispatch(async () =>
        {
            await using var model = new MainWindowViewModel();
            var view = new MainView { DataContext = model };
            var window = new Window { Content = view, Width = 1280, Height = 800 };
            var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            view.Loaded += (_, _) => loaded.TrySetResult();
            window.Show();
            window.UpdateLayout();
            await loaded.Task.WaitAsync(TestContext.Current.CancellationToken);
            var fill = view.FindControl<CheckBox>("FillSetting")!;
            var width = view.FindControl<StackPanel>("WidthSettings")!;
            Assert.False(fill.IsVisible);
            Assert.True(width.IsVisible);

            view.FindControl<Button>("RectangleTool")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(fill.IsVisible);
            model.FillShapes = true;
            view.FindControl<Button>("LineTool")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.False(fill.IsVisible);
            Assert.True(width.IsVisible);
            view.FindControl<Button>("SelectTool")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.False(width.IsVisible);
            Assert.True(model.FillShapes);
            Assert.Equal(0, model.OperationCount);
            window.Close();
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task SelectingObjectKeepsCanvasOriginDuringDrag()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(EditorTests));
        await session.Dispatch(async () =>
        {
            await using var model = new MainWindowViewModel();
            model.AddShape(P2PDraw.Core.Models.ShapeKind.Rectangle, new(20, 20), new(80, 80));
            var view = new MainView { DataContext = model };
            var window = new Window { Content = view, Width = 1280, Height = 800 };
            var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            view.Loaded += (_, _) => loaded.TrySetResult();
            window.Show();
            window.UpdateLayout();
            await loaded.Task.WaitAsync(TestContext.Current.CancellationToken);
            view.FindControl<Button>("SelectTool")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.UpdateLayout();
            var canvas = view.FindControl<DrawingCanvas>("DrawingSurface")!;
            Point origin = canvas.TranslatePoint(default, window)!.Value;
            Point grab = canvas.TranslatePoint(new Point(20, 50), window)!.Value;

            window.MouseDown(grab, MouseButton.Left);
            window.UpdateLayout();

            Point selectedOrigin = canvas.TranslatePoint(default, window)!.Value;
            Assert.Equal(origin, selectedOrigin);
            var slider = view.FindControl<Slider>("BrushSize")!;
            var thumb = slider.GetVisualDescendants().OfType<Thumb>().First();
            Point thumbTop = thumb.TranslatePoint(default, slider)!.Value;
            Point thumbBottom = thumb.TranslatePoint(new Point(thumb.Bounds.Width, thumb.Bounds.Height), slider)!.Value;
            Assert.InRange(thumbTop.Y, 0, slider.Bounds.Height);
            Assert.InRange(thumbBottom.Y, 0, slider.Bounds.Height);
            window.MouseMove(grab + new Vector(20, 30), RawInputModifiers.LeftMouseButton);
            window.MouseUp(grab + new Vector(20, 30), MouseButton.Left);
            Assert.Equal(new P2PDraw.Core.Models.StrokePoint(20, 30), Assert.Single(model.Shapes).Offset);
            window.Close();
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task RecreatedViewHandlesBackWithoutCallingDetachedView()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(EditorTests));
        await session.Dispatch(async () =>
        {
            var app = (P2PDraw.App.App)Application.Current!;
            var firstView = app.CreateMobileView();
            var model = (MainWindowViewModel)firstView.DataContext!;
            var window = new Window { Content = firstView, Width = 393, Height = 851 };
            try
            {
                var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                firstView.Loaded += (_, _) => loaded.TrySetResult();
                window.Show();
                window.UpdateLayout();
                await loaded.Task.WaitAsync(TestContext.Current.CancellationToken);
                firstView.FindControl<Button>("SessionButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                window.UpdateLayout();
                string sessionId = model.SessionId;

                window.Content = null;
                var recreatedView = app.CreateMobileView();
                window.Content = recreatedView;
                window.UpdateLayout();
                recreatedView.FindControl<Button>("FilesButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                window.UpdateLayout();
                var back = new RoutedEventArgs(TopLevel.BackRequestedEvent);
                window.RaiseEvent(back);
                window.UpdateLayout();

                Assert.True(back.Handled);
                Assert.False(recreatedView.FindControl<Border>("SidePanel")!.IsVisible);
                Assert.True(firstView.FindControl<Border>("SidePanel")!.IsVisible);
                Assert.Same(model, recreatedView.DataContext);
                Assert.Equal(sessionId, model.SessionId);
                Assert.Equal(0, model.OperationCount);
            }
            finally
            {
                await app.CloseMobileEditorAsync();
                window.Close();
            }
            return true;
        }, TestContext.Current.CancellationToken);
    }
}
