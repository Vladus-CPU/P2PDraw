using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using P2PDraw.App.Controls;
using P2PDraw.App.ViewModels;
using P2PDraw.App.Views;
using P2PDraw.Core.Models;
using Xunit;

namespace P2PDraw.Tests.App;

public partial class EditorTests
{
    [Fact]
    public async Task RecreatedMobileViewClearsSelectionAndRetainsLayerViewState()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(EditorTests));
        await session.Dispatch(async () =>
        {
            var app = (P2PDraw.App.App)Application.Current!;
            var firstView = app.CreateMobileView();
            var model = (MainWindowViewModel)firstView.DataContext!;
            var window = new Window { Content = firstView, Width = 412, Height = 915 };
            try
            {
                model.LayerEditor.AddLayer();
                Guid activeLayer = model.LayerEditor.ActiveLayerId;
                model.LayerEditor.ToggleVisibility(Layer.DefaultLayerId);
                model.LayerEditor.ToggleLock(Layer.DefaultLayerId);
                model.FillShapes = true;
                model.AddShape(ShapeKind.Rectangle, new(20, 20), new(80, 80));
                Guid shapeId = Assert.Single(model.Shapes).ShapeId;
                window.Show();
                window.UpdateLayout();
                var firstCanvas = firstView.FindControl<DrawingCanvas>("DrawingSurface")!;
                firstCanvas.Tool = DrawingTool.Select;
                Point position = firstCanvas.TranslatePoint(new Point(50, 50), window)!.Value;
                using var finger = window.TouchBegin(position);
                window.TouchEnd(finger, position);
                Assert.Equal(shapeId, firstCanvas.SelectedObjectId);
                Assert.Equal(shapeId, model.LayerEditor.SelectedObjectId);
                Assert.True(model.LayerEditor.CanMoveObject);
                string document = Normalize(model);
                string sessionId = model.SessionId;

                window.Content = null;
                var recreatedView = app.CreateMobileView();
                window.Content = recreatedView;
                window.UpdateLayout();

                var recreatedCanvas = recreatedView.FindControl<DrawingCanvas>("DrawingSurface")!;
                Assert.Same(model, recreatedView.DataContext);
                Assert.Null(recreatedCanvas.SelectedObjectId);
                Assert.Null(model.LayerEditor.SelectedObjectId);
                Assert.False(model.LayerEditor.CanMoveObject);
                var panel = recreatedView.FindControl<LayersPanel>("LayerSide")!;
                Assert.False(panel.FindControl<Button>("MoveToLayerButton")!.IsEnabled);
                Assert.False(recreatedView.FindControl<Button>("DeleteButton")!.IsEnabled);
                Assert.False(recreatedView.FindControl<Button>("DuplicateButton")!.IsEnabled);
                Assert.Equal(activeLayer, model.LayerEditor.ActiveLayerId);
                var defaultLayer = model.LayerEditor.Layers.Single(layer => layer.LayerId == Layer.DefaultLayerId);
                Assert.False(defaultLayer.IsVisible);
                Assert.True(defaultLayer.IsLocked);
                Assert.Equal(document, Normalize(model));
                Assert.Equal(sessionId, model.SessionId);
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
