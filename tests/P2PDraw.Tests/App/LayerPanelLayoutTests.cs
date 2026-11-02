using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using P2PDraw.App.ViewModels;
using P2PDraw.App.Views;
using Xunit;

namespace P2PDraw.Tests.App;

public partial class EditorTests
{
    [Theory]
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
                Assert.InRange(rowTop.Y, 0, list.Bounds.Height);
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
                more.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                window.UpdateLayout();
                var actions = panel.FindControl<ScrollViewer>("LayerActionsScroll")!;
                var list = panel.FindControl<ListBox>("LayerList")!;
                Assert.True(actions.IsVisible);
                Assert.False(list.IsVisible);
                Assert.True(actions.Viewport.Height > 0);
                Assert.True(actions.Extent.Height > actions.Viewport.Height);

                actions.Offset = new Vector(0, actions.Extent.Height - actions.Viewport.Height);
                window.UpdateLayout();
                var remove = panel.FindControl<Button>("RemoveLayerButton")!;
                var bottom = remove.TranslatePoint(new Point(0, remove.Bounds.Height), actions)!.Value;
                Assert.InRange(bottom.Y, 1, actions.Bounds.Height);
                Assert.True(panel.CloseMenu());
                window.UpdateLayout();
                Assert.True(list.IsVisible);
                Assert.False(actions.IsVisible);
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
                more.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                panel.FindControl<Button>("ClearLayerButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(1, clearRequests);
                Assert.False(panel.FindControl<ScrollViewer>("LayerActionsScroll")!.IsVisible);

                more.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
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
