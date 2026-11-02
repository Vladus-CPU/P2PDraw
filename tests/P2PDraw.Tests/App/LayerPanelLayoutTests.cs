using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using P2PDraw.App.ViewModels;
using P2PDraw.App.Views;
using Xunit;

namespace P2PDraw.Tests.App;

public partial class EditorTests
{
    [Fact]
    public async Task HeaderAddSelectsNewLayerAndRemoteRefreshKeepsActiveRow()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(EditorTests));
        await session.Dispatch(async () =>
        {
            string sessionId = Guid.NewGuid().ToString();
            await using var first = new MainWindowViewModel(["0", sessionId, "1"]);
            await using var second = new MainWindowViewModel(["0", sessionId, "1"]);
            var panel = new LayersPanel { DataContext = first.LayerEditor };
            var window = new Window { Content = panel, Width = 280, Height = 500 };
            try
            {
                window.Show();
                window.UpdateLayout();
                await first.StartListeningAsync();
                await second.StartListeningAsync();
                second.RemotePort = first.LocalPort;
                await second.ConnectAsync();
                var list = panel.FindControl<ListBox>("LayerList")!;
                var add = panel.FindControl<Button>("AddLayerButton")!;
                Point center = add.TranslatePoint(new Point(add.Bounds.Width / 2, add.Bounds.Height / 2), window)!.Value;
                for (int count = 2; count <= 3; count++)
                {
                    window.MouseDown(center, MouseButton.Left);
                    window.MouseUp(center, MouseButton.Left);
                    await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Loaded);
                    Assert.Equal(count, list.ItemCount);
                    Assert.Equal(first.LayerEditor.Layers[0].LayerId, first.LayerEditor.ActiveLayerId);
                    Assert.Equal(first.LayerEditor.SelectedLayer, list.SelectedItem);
                    for (int index = 0; index < count; index++)
                    {
                        var row = Assert.IsType<ListBoxItem>(list.ContainerFromIndex(index));
                        var layer = Assert.IsType<LayerEntry>(list.Items[index]);
                        Assert.Equal(layer.LayerId == first.LayerEditor.ActiveLayerId, row.IsSelected);
                    }
                }

                Guid activeLayerId = first.LayerEditor.ActiveLayerId;
                await WaitUntilAsync(() => second.LayerEditor.Layers.Length == 3);
                second.LayerEditor.AddLayer();
                await WaitUntilAsync(() => first.LayerEditor.Layers.Length == 4);
                await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Loaded);
                Assert.Equal(activeLayerId, first.LayerEditor.ActiveLayerId);
                Assert.Equal(first.LayerEditor.SelectedLayer, list.SelectedItem);
                for (int index = 0; index < list.ItemCount; index++)
                {
                    var row = Assert.IsType<ListBoxItem>(list.ContainerFromIndex(index));
                    var layer = Assert.IsType<LayerEntry>(list.Items[index]);
                    Assert.Equal(layer.LayerId == activeLayerId, row.IsSelected);
                }
            }
            finally
            {
                window.Close();
            }
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(20)]
    public async Task LayerRowsStayVisibleWhenAdditionalActionsOpen(int layerCount)
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(EditorTests));
        await session.Dispatch(async () =>
        {
            await using var model = new MainWindowViewModel();
            for (int index = 1; index < layerCount; index++)
            {
                model.LayerEditor.AddLayer();
            }
            var panel = new LayersPanel { DataContext = model.LayerEditor };
            var window = new Window { Content = panel, Width = 280, Height = 500 };
            try
            {
                window.Show();
                window.UpdateLayout();
                var list = panel.FindControl<ListBox>("LayerList")!;
                Assert.Equal(layerCount, list.ItemCount);
                for (int index = 0; index < layerCount; index++)
                {
                    list.ScrollIntoView(index);
                    window.UpdateLayout();
                    var row = Assert.IsType<ListBoxItem>(list.ContainerFromIndex(index));
                    string name = model.LayerEditor.Layers[index].Name;
                    Assert.Contains(row.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == name);
                }
                Assert.Equal(model.LayerEditor.SelectedLayer, list.SelectedItem);
                var add = panel.FindControl<Button>("AddLayerButton")!;
                var addBottom = add.TranslatePoint(new Point(0, add.Bounds.Height), panel)!.Value;
                var listTop = list.TranslatePoint(default, panel)!.Value;
                Assert.True(addBottom.Y <= listTop.Y);
                var more = panel.FindControl<Button>("MoreLayerButton")!;
                Point moreCenter = more.TranslatePoint(new Point(more.Bounds.Width / 2, more.Bounds.Height / 2), window)!.Value;
                window.MouseDown(moreCenter, MouseButton.Left);
                window.MouseUp(moreCenter, MouseButton.Left);
                window.UpdateLayout();

                Assert.True(Assert.IsType<Flyout>(more.Flyout).IsOpen);
                Assert.True(list.IsEffectivelyVisible);
                Assert.Equal(layerCount, list.ItemCount);
                Assert.Equal(model.LayerEditor.LayerName, panel.FindControl<TextBox>("LayerNameBox")!.Text);
                Assert.True(panel.FindControl<Button>("RenameLayerButton")!.IsEnabled);
                window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
                window.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
                Assert.False(Assert.IsType<Flyout>(more.Flyout).IsOpen);
                Assert.True(list.IsEffectivelyVisible);
            }
            finally
            {
                window.Close();
            }
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(393, 851, 500)]
    [InlineData(844, 600, 280)]
    public async Task RenamePopupKeepsFocusedFieldVisibleWhenViewportShrinks(int width, int height, int reducedHeight)
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(EditorTests));
        await session.Dispatch(async () =>
        {
            await using var model = new MainWindowViewModel();
            var view = new MainView { DataContext = model };
            var window = new Window { Content = view, Width = width, Height = height };
            try
            {
                window.Show();
                window.UpdateLayout();
                view.FindControl<Button>("LayersButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                window.UpdateLayout();
                var panel = view.FindControl<LayersPanel>("LayerSide")!;
                var more = panel.FindControl<Button>("MoreLayerButton")!;
                Point moreCenter = more.TranslatePoint(new Point(more.Bounds.Width / 2, more.Bounds.Height / 2), window)!.Value;
                window.MouseDown(moreCenter, MouseButton.Left);
                window.MouseUp(moreCenter, MouseButton.Left);
                window.UpdateLayout();
                var field = panel.FindControl<TextBox>("LayerNameBox")!;
                Assert.True(field.Focus());
                window.Height = reducedHeight;
                window.UpdateLayout();
                var popup = TopLevel.GetTopLevel(field)!;
                popup.UpdateLayout();

                var viewport = panel.FindControl<ScrollViewer>("LayerActionsScroll")!;
                Point top = field.TranslatePoint(default, viewport)!.Value;
                Point bottom = field.TranslatePoint(new Point(field.Bounds.Width, field.Bounds.Height), viewport)!.Value;
                Assert.True(field.IsFocused);
                Assert.InRange(top.Y, 0, viewport.Bounds.Height);
                Assert.InRange(bottom.Y, 0, viewport.Bounds.Height);
                Assert.True(popup.Bounds.Height <= window.Bounds.Height);
                Assert.True(view.TryClosePanel());
                Assert.False(Assert.IsType<Flyout>(more.Flyout).IsOpen);
                Assert.True(panel.IsVisible);
                Assert.True(view.TryClosePanel());
                Assert.False(panel.IsVisible);
            }
            finally
            {
                window.Close();
            }
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task RemoteLayerRowsRefreshAfterResizingAndSwitchingPanels()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(EditorTests));
        await session.Dispatch(async () =>
        {
            string sessionId = Guid.NewGuid().ToString();
            await using var first = new MainWindowViewModel(["0", sessionId, "1"]);
            await using var second = new MainWindowViewModel(["0", sessionId, "1"]);
            var view = new MainView { DataContext = first };
            var window = new Window { Content = view, Width = 1280, Height = 800 };
            try
            {
                window.Show();
                window.UpdateLayout();
                await first.StartListeningAsync();
                await second.StartListeningAsync();
                second.RemotePort = first.LocalPort;
                await second.ConnectAsync();
                for (int index = 0; index < 4; index++)
                {
                    second.LayerEditor.AddLayer();
                }
                await WaitUntilAsync(() => first.LayerEditor.Layers.Length == 5);
                window.UpdateLayout();
                var panel = view.FindControl<LayersPanel>("LayerSide")!;
                var list = panel.FindControl<ListBox>("LayerList")!;
                Assert.Equal(5, list.ItemCount);

                view.FindControl<Button>("SessionButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                window.Width = 393;
                window.Height = 740;
                await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Loaded);
                view.FindControl<Button>("LayersButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                window.UpdateLayout();
                list.SelectedIndex = 0;
                window.UpdateLayout();
                var more = panel.FindControl<Button>("MoreLayerButton")!;
                Point moreCenter = more.TranslatePoint(new Point(more.Bounds.Width / 2, more.Bounds.Height / 2), window)!.Value;
                window.MouseDown(moreCenter, MouseButton.Left);
                window.MouseUp(moreCenter, MouseButton.Left);
                window.UpdateLayout();
                Assert.True(Assert.IsType<Flyout>(more.Flyout).IsOpen, $"Panel visible: {panel.IsVisible}; More: {more.Bounds}; pointer: {moreCenter}; window: {window.Bounds}");
                second.LayerEditor.LayerName = "Віддалений шар";
                second.LayerEditor.RenameLayer();
                await WaitUntilAsync(() => first.LayerEditor.Layers[0].Name == "Віддалений шар");
                window.UpdateLayout();
                Assert.Equal("Віддалений шар", panel.FindControl<TextBox>("LayerNameBox")!.Text);
                Assert.True(panel.CloseMenu());
                list.ScrollIntoView(0);
                window.UpdateLayout();
                var row = Assert.IsType<ListBoxItem>(list.ContainerFromIndex(0));
                Assert.Contains(row.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "Віддалений шар");
                second.LayerEditor.MoveLayer(false);
                await WaitUntilAsync(() => first.LayerEditor.Layers[1].Name == "Віддалений шар");
                window.UpdateLayout();
                list.ScrollIntoView(1);
                window.UpdateLayout();
                row = Assert.IsType<ListBoxItem>(list.ContainerFromIndex(1));
                Assert.Contains(row.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "Віддалений шар");
                Guid removed = second.LayerEditor.ActiveLayerId;
                second.LayerEditor.RemoveLayer(removed);
                await WaitUntilAsync(() => first.LayerEditor.Layers.Length == 4);
                window.UpdateLayout();
                Assert.Equal(4, list.ItemCount);
                Assert.DoesNotContain(list.Items, item => item is LayerEntry layer && layer.LayerId == removed);
                Assert.True(panel.IsVisible);
                Assert.True(list.IsEffectivelyVisible);
            }
            finally
            {
                window.Close();
            }
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ReplacingViewClosesLayerPopupAndRemovesOldViewportSubscription()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(EditorTests));
        await session.Dispatch(async () =>
        {
            await using var model = new MainWindowViewModel();
            var view = new MainView { DataContext = model };
            var window = new Window { Content = view, Width = 393, Height = 740 };
            try
            {
                window.Show();
                window.UpdateLayout();
                view.FindControl<Button>("LayersButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                window.UpdateLayout();
                var panel = view.FindControl<LayersPanel>("LayerSide")!;
                var more = panel.FindControl<Button>("MoreLayerButton")!;
                var menu = Assert.IsType<Flyout>(more.Flyout);
                Point center = more.TranslatePoint(new Point(more.Bounds.Width / 2, more.Bounds.Height / 2), window)!.Value;
                window.MouseDown(center, MouseButton.Left);
                window.MouseUp(center, MouseButton.Left);
                Assert.True(menu.IsOpen);
                double previousMaxHeight = panel.FindControl<ScrollViewer>("LayerActionsScroll")!.MaxHeight;

                window.Content = new MainView { DataContext = model };
                window.UpdateLayout();
                Assert.False(menu.IsOpen);
                window.Height = 500;
                await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Loaded);
                Assert.Equal(previousMaxHeight, panel.FindControl<ScrollViewer>("LayerActionsScroll")!.MaxHeight);
            }
            finally
            {
                window.Close();
            }
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task MoreLayerButtonUsesStandardFlyoutToggle()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(EditorTests));
        await session.Dispatch(async () =>
        {
            await using var model = new MainWindowViewModel();
            var panel = new LayersPanel { DataContext = model.LayerEditor };
            var window = new Window { Content = panel, Width = 280, Height = 500 };
            try
            {
                window.Show();
                window.UpdateLayout();
                var more = panel.FindControl<Button>("MoreLayerButton")!;
                var menu = Assert.IsType<Flyout>(more.Flyout);
                Point center = more.TranslatePoint(new Point(more.Bounds.Width / 2, more.Bounds.Height / 2), window)!.Value;
                window.MouseDown(center, MouseButton.Left);
                window.MouseUp(center, MouseButton.Left);
                Assert.True(menu.IsOpen);
                window.MouseDown(center, MouseButton.Left);
                window.MouseUp(center, MouseButton.Left);
                Assert.False(menu.IsOpen);
                Assert.True(panel.FindControl<ListBox>("LayerList")!.IsEffectivelyVisible);
            }
            finally
            {
                window.Close();
            }
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(240, 160)]
    [InlineData(260, 172)]
    [InlineData(240, 240)]
    [InlineData(280, 650)]
    public async Task LayerListScrollsWithoutCoveringPermanentActions(double width, double height)
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(EditorTests));
        await session.Dispatch(async () =>
        {
            await using var model = new MainWindowViewModel();
            for (int index = 0; index < 20; index++)
            {
                model.LayerEditor.AddLayer();
            }
            var panel = new LayersPanel { DataContext = model.LayerEditor };
            var window = new Window { Content = panel, Width = width, Height = height };
            try
            {
                window.Show();
                window.UpdateLayout();
                var list = panel.FindControl<ListBox>("LayerList")!;
                var toolbar = panel.FindControl<Grid>("LayerToolbar")!;
                var listBottom = list.TranslatePoint(new Point(0, list.Bounds.Height), panel)!.Value;
                var toolbarTop = toolbar.TranslatePoint(default, panel)!.Value;
                Assert.True(list.Bounds.Height > 0);
                Assert.True(listBottom.Y <= toolbarTop.Y);

                foreach (string name in new[] { "AddLayerButton", "RaiseLayerButton", "LowerLayerButton", "MoreLayerButton" })
                {
                    var button = panel.FindControl<Button>(name)!;
                    Assert.True(button.Bounds.Width >= 48, name);
                    Assert.True(button.Bounds.Height >= 48, name);
                    var bottom = button.TranslatePoint(new Point(0, button.Bounds.Height), panel)!.Value;
                    Assert.True(bottom.Y <= panel.Bounds.Height, name);
                }

                list.ScrollIntoView(list.ItemCount - 1);
                window.UpdateLayout();
                var lastRow = Assert.IsType<ListBoxItem>(list.ContainerFromIndex(list.ItemCount - 1));
                var rowTop = lastRow.TranslatePoint(default, list)!.Value;
                var rowBottom = lastRow.TranslatePoint(new Point(0, lastRow.Bounds.Height), list)!.Value;
                Assert.InRange(rowTop.Y, 0, list.Bounds.Height);
                Assert.True(rowBottom.Y <= list.Bounds.Height + 0.01);
                foreach (var button in lastRow.GetVisualDescendants().OfType<Button>())
                {
                    Assert.True(button.Bounds.Width >= 48);
                    Assert.True(button.Bounds.Height >= 48);
                }
            }
            finally
            {
                window.Close();
            }
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(640, 360)]
    [InlineData(844, 390)]
    public async Task LandscapeEditorKeepsCompleteLayerRowAndActions(double width, double height)
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(EditorTests));
        await session.Dispatch(async () =>
        {
            await using var model = new MainWindowViewModel();
            var view = new MainView { DataContext = model };
            var window = new Window { Content = view, Width = width, Height = height };
            try
            {
                window.Show();
                window.UpdateLayout();
                view.FindControl<Button>("LayersButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                window.UpdateLayout();
                var panel = view.FindControl<LayersPanel>("LayerSide")!;
                var list = panel.FindControl<ListBox>("LayerList")!;
                var row = Assert.IsType<ListBoxItem>(list.ContainerFromIndex(0));
                var rowBottom = row.TranslatePoint(new Point(0, row.Bounds.Height), list)!.Value;
                Assert.True(panel.IsVisible);
                Assert.InRange(rowBottom.Y, 1, list.Bounds.Height);
                var more = panel.FindControl<Button>("MoreLayerButton")!;
                var moreBottom = more.TranslatePoint(new Point(0, more.Bounds.Height), panel)!.Value;
                Assert.True(more.Bounds.Width >= 48);
                Assert.True(more.Bounds.Height >= 48);
                Assert.InRange(moreBottom.Y, 1, panel.Bounds.Height);
            }
            finally
            {
                window.Close();
            }
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task LayerActionsRemainScrollableAtLowHeightAndReturnToList()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(EditorTests));
        await session.Dispatch(async () =>
        {
            await using var model = new MainWindowViewModel();
            var panel = new LayersPanel { DataContext = model.LayerEditor };
            var window = new Window { Content = panel, Width = 260, Height = 250 };
            try
            {
                window.Show();
                window.UpdateLayout();
                var more = panel.FindControl<Button>("MoreLayerButton")!;
                var menu = Assert.IsType<Flyout>(more.Flyout);
                var list = panel.FindControl<ListBox>("LayerList")!;
                double listHeight = list.Bounds.Height;
                Point moreCenter = more.TranslatePoint(new Point(more.Bounds.Width / 2, more.Bounds.Height / 2), window)!.Value;
                window.MouseDown(moreCenter, MouseButton.Left);
                window.MouseUp(moreCenter, MouseButton.Left);
                window.UpdateLayout();
                var actions = panel.FindControl<ScrollViewer>("LayerActionsScroll")!;
                Assert.True(menu.IsOpen);
                Assert.True(list.IsVisible);
                Assert.Equal(listHeight, list.Bounds.Height);
                Assert.True(actions.Viewport.Height > 0);
                Assert.True(actions.Extent.Height > actions.Viewport.Height);
                Assert.True(actions.Bounds.Height <= window.Bounds.Height);

                actions.Offset = new Vector(0, actions.Extent.Height - actions.Viewport.Height);
                window.UpdateLayout();
                var remove = panel.FindControl<Button>("RemoveLayerButton")!;
                var bottom = remove.TranslatePoint(new Point(0, remove.Bounds.Height), actions)!.Value;
                Assert.InRange(bottom.Y, 1, actions.Bounds.Height);
                Assert.True(panel.CloseMenu());
                window.UpdateLayout();
                Assert.True(list.IsVisible);
                Assert.False(menu.IsOpen);
                Assert.False(panel.CloseMenu());
            }
            finally
            {
                window.Close();
            }
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task LayerMenuRequestsConfirmationAndDoesNotDeleteDirectly()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(EditorTests));
        await session.Dispatch(async () =>
        {
            await using var model = new MainWindowViewModel();
            var panel = new LayersPanel { DataContext = model.LayerEditor };
            int clearRequests = 0;
            int removeRequests = 0;
            panel.ClearRequested += () => clearRequests++;
            panel.RemoveRequested += () => removeRequests++;
            var window = new Window { Content = panel, Width = 260, Height = 500 };
            try
            {
                window.Show();
                window.UpdateLayout();
                var more = panel.FindControl<Button>("MoreLayerButton")!;
                var menu = Assert.IsType<Flyout>(more.Flyout);
                Point moreCenter = more.TranslatePoint(new Point(more.Bounds.Width / 2, more.Bounds.Height / 2), window)!.Value;
                window.MouseDown(moreCenter, MouseButton.Left);
                window.MouseUp(moreCenter, MouseButton.Left);
                panel.FindControl<Button>("ClearLayerButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(1, clearRequests);
                Assert.False(menu.IsOpen);

                window.MouseDown(moreCenter, MouseButton.Left);
                window.MouseUp(moreCenter, MouseButton.Left);
                panel.FindControl<Button>("RemoveLayerButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(1, removeRequests);
                Assert.Single(model.LayerEditor.Layers);
                Assert.Equal(0, model.OperationCount);
            }
            finally
            {
                window.Close();
            }
            return true;
        }, TestContext.Current.CancellationToken);
    }
}
