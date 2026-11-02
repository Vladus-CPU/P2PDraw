using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using P2PDraw.App.ViewModels;
using P2PDraw.App.Views;
using P2PDraw.Core.Models;
using Xunit;

namespace P2PDraw.Tests.App;

public partial class EditorTests
{
    [Fact]
    public async Task LayerPanelSharesEditsButKeepsVisibilityAndLockLocal()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(EditorTests));
        await session.Dispatch(async () =>
        {
            string sessionId = Guid.NewGuid().ToString();
            await using var first = new MainWindowViewModel(["0", sessionId, "1"]);
            await using var second = new MainWindowViewModel(["0", sessionId, "1"]);
            var view = new MainView { DataContext = first };
            var window = new Window { Content = view, Width = 412, Height = 915 };
            try
            {
                window.Show();
                window.UpdateLayout();
                await first.StartListeningAsync();
                await second.StartListeningAsync();
                first.RemotePort = second.LocalPort;
                await first.ConnectAsync();
                first.LayerEditor.AddLayer();
                Guid layerId = first.LayerEditor.ActiveLayerId;
                first.AddShape(ShapeKind.Rectangle, new(10, 20), new(30, 40));
                second.BeginStroke(new(60, 70));
                second.EndStroke();
                await WaitUntilAsync(() => first.OperationCount == 5 && second.OperationCount == 5);
                Assert.Equal(layerId, Assert.Single(second.Shapes).LayerId);
                Assert.Equal(Layer.DefaultLayerId, second.LayerEditor.ActiveLayerId);

                first.LayerEditor.LayerName = "Контури";
                first.LayerEditor.RenameLayer();
                Assert.Equal(6, first.OperationCount);
                Assert.Equal(layerId, first.LayerEditor.ActiveLayerId);
                Assert.True(first.LayerEditor.CanLower);
                first.LayerEditor.MoveLayer(false);
                Assert.Equal(7, first.OperationCount);
                await WaitUntilAsync(() => second.OperationCount == 7);
                Assert.Equal("Контури", second.GetSnapshot().Layers[0].Name);
                first.LayerEditor.ToggleVisibility(layerId);
                Assert.False(first.LayerEditor.CanDraw);
                Assert.False(first.BeginStroke(new(1, 2)));
                first.AddShape(ShapeKind.Line, new(1, 2), new(3, 4));
                Assert.Equal(7, first.OperationCount);
                Assert.Contains(layerId, second.LayerEditor.VisibleLayerIds);
                first.LayerEditor.ToggleVisibility(layerId);
                first.LayerEditor.ToggleLock(layerId);
                Assert.False(first.BeginStroke(new(1, 2)));
                Assert.Contains(layerId, second.LayerEditor.SelectableLayerIds);
                first.LayerEditor.ToggleLock(layerId);

                Assert.False(view.FindControl<LayersPanel>("LayerSide")!.IsVisible);
                view.FindControl<Button>("LayersButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var panel = view.FindControl<LayersPanel>("LayerSide")!;
                Assert.True(panel.IsVisible);
                panel.FindControl<Button>("ClearLayerButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Single(first.Shapes);
                view.FindControl<Button>("ConfirmActionButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await WaitUntilAsync(() => second.OperationCount == 8);
                Assert.Empty(second.Shapes);
                Assert.Single(second.Strokes);
                Assert.Equal(Normalize(first), Normalize(second));
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
    public async Task LayerMembershipAndLastLayerDeletionRemainUsable()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(EditorTests));
        await session.Dispatch(async () =>
        {
            await using var model = new MainWindowViewModel();
            model.AddShape(ShapeKind.Ellipse, new(1, 2), new(3, 4));
            Guid objectId = Assert.Single(model.Shapes).ShapeId;
            model.LayerEditor.AddLayer();
            Guid targetLayer = model.LayerEditor.ActiveLayerId;
            model.LayerEditor.SelectedObjectId = objectId;
            model.LayerEditor.MoveSelectedObject();
            Assert.Equal(targetLayer, Assert.Single(model.Shapes).LayerId);
            model.LayerEditor.RemoveLayer(targetLayer);
            Assert.Empty(model.Shapes);
            Assert.Equal(Layer.DefaultLayerId, model.LayerEditor.ActiveLayerId);
            model.LayerEditor.RemoveLayer(Layer.DefaultLayerId);
            Assert.Empty(model.LayerEditor.Layers);
            Assert.False(model.BeginStroke(new(1, 2)));
            model.LayerEditor.AddLayer();
            Assert.True(model.BeginStroke(new(1, 2)));
            model.EndStroke();
            Assert.Single(model.Strokes);
            return true;
        }, TestContext.Current.CancellationToken);
    }
}
