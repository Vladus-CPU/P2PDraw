using P2PDraw.App.ViewModels;
using P2PDraw.Core.Models;
using P2PDraw.Core.Operations;
using P2PDraw.Core.Sync;
using Xunit;

namespace P2PDraw.Tests.App;

public class LayerOrderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EqualPositionsMoveOneLayerAndConvergeWithConcurrentProperties(bool raise)
    {
        var engine = new SyncEngine(Guid.NewGuid(), Guid.NewGuid());
        engine.AddLayer("Перший", 1);
        engine.AddLayer("Другий", 1);
        engine.AddLayer("Третій", 1);
        var original = engine.GetLayers();
        var initialSnapshot = engine.CreateSnapshot();
        var published = new List<Operation>();
        var model = new LayersViewModel(() => engine, () => true, published.Add);
        int index = raise ? 1 : 3;
        Guid activeId = original[index].LayerId;
        model.SelectedLayer = model.Layers.Single(layer => layer.LayerId == activeId);
        var desired = original.Select(layer => layer.LayerId).ToArray();
        int neighbor = index + (raise ? 1 : -1);
        desired[index] = original[neighbor].LayerId;
        desired[neighbor] = activeId;

        model.MoveLayer(raise);

        Assert.Equal(desired, engine.GetLayers().Select(layer => layer.LayerId));
        Assert.Equal(activeId, model.ActiveLayerId);
        Assert.Equal(2, published.Count);
        foreach (var operation in published)
        {
            var reorder = Assert.IsType<LayerReorder>(operation);
            var previous = original.Single(layer => layer.LayerId == reorder.LayerId);
            Assert.NotEqual(previous.Position, reorder.Position);
        }
        var remote = new SyncEngine(Guid.NewGuid(), engine.SessionId);
        remote.MergeSnapshot(initialSnapshot);
        var rename = remote.RenameLayer(activeId, "Змінено іншим учасником");
        var reordered = remote.SetLayerPosition(activeId, 10);
        engine.Receive(rename);
        engine.Receive(reordered);
        var receiver = new SyncEngine(Guid.NewGuid(), engine.SessionId);
        foreach (var operation in engine.GetOperations().Reverse())
        {
            receiver.Receive(operation);
        }
        Assert.Equal(engine.GetLayers(), receiver.GetLayers());
        Assert.Equal(rename.Name, receiver.GetLayers().Single(layer => layer.LayerId == activeId).Name);
        Assert.Empty(receiver.GetPendingLayerIds());
    }

    [Fact]
    public void AdjacentFloatingPointPositionsUseNormalizedRanks()
    {
        var engine = new SyncEngine(Guid.NewGuid(), Guid.NewGuid());
        double next = Math.BitIncrement(1);
        var first = engine.AddLayer("Перший", 1);
        var second = engine.AddLayer("Другий", next);
        var third = engine.AddLayer("Третій", Math.BitIncrement(next));
        var published = new List<Operation>();
        var model = new LayersViewModel(() => engine, () => true, published.Add);
        model.SelectedLayer = model.Layers.Single(layer => layer.LayerId == first.LayerId);

        model.MoveLayer(true);

        Guid[] expected = [Layer.DefaultLayerId, second.LayerId, first.LayerId, third.LayerId];
        Assert.Equal(expected, engine.GetLayers().Select(layer => layer.LayerId));
        Assert.Equal(new double[] { 0, 1, 2, 3 }, engine.GetLayers().Select(layer => layer.Position));
        Assert.Equal(3, published.Count);
    }
}
