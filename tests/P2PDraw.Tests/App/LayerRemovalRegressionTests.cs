using Avalonia.Headless;
using P2PDraw.App.ViewModels;
using P2PDraw.Core.Models;
using P2PDraw.Core.Sync;
using Xunit;

namespace P2PDraw.Tests.App;

public partial class EditorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StrokeStartHandlesRemoteLayerRemovalBeforeUiRefresh(bool useNewLayer)
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(EditorTests));
        await session.Dispatch(async () =>
        {
            await using var model = new MainWindowViewModel();
            if (useNewLayer)
            {
                model.LayerEditor.AddLayer();
            }
            var engine = model.Session.Engine;
            var remote = new SyncEngine(Guid.NewGuid(), engine.SessionId);
            remote.MergeSnapshot(model.GetSnapshot());
            engine.Receive(remote.RemoveLayer(model.LayerEditor.ActiveLayerId));
            Assert.True(model.LayerEditor.CanDraw);
            long clock = engine.LamportTime;
            int operationCount = engine.GetOperations().Length;

            Assert.False(model.BeginStroke(new(10, 20)));

            Assert.Empty(model.Strokes);
            Assert.Equal(clock, engine.LamportTime);
            Assert.Equal(operationCount, engine.GetOperations().Length);
            Assert.Contains("шар видалено", model.Status);
            Assert.Equal(useNewLayer, model.LayerEditor.CanDraw);
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(ShapeKind.Line)]
    [InlineData(ShapeKind.Rectangle)]
    [InlineData(ShapeKind.Ellipse)]
    public async Task ShapeCreationHandlesRemoteLayerRemovalBeforeUiRefresh(ShapeKind kind)
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(EditorTests));
        await session.Dispatch(async () =>
        {
            await using var model = new MainWindowViewModel();
            model.LayerEditor.AddLayer();
            var engine = model.Session.Engine;
            var remote = new SyncEngine(Guid.NewGuid(), engine.SessionId);
            remote.MergeSnapshot(model.GetSnapshot());
            engine.Receive(remote.RemoveLayer(model.LayerEditor.ActiveLayerId));
            Assert.True(model.LayerEditor.CanDraw);
            long clock = engine.LamportTime;
            int operationCount = engine.GetOperations().Length;

            model.AddShape(kind, new(10, 20), new(30, 40));

            Assert.Empty(model.Shapes);
            Assert.Equal(clock, engine.LamportTime);
            Assert.Equal(operationCount, engine.GetOperations().Length);
            Assert.Contains("шар видалено", model.Status);
            Assert.Equal(Layer.DefaultLayerId, model.LayerEditor.ActiveLayerId);
            return true;
        }, TestContext.Current.CancellationToken);
    }
}
