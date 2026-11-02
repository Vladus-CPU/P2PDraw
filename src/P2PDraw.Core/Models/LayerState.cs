using System.Collections.Immutable;
using P2PDraw.Core.Operations;

namespace P2PDraw.Core.Models;

internal sealed class LayerState
{
    private readonly Dictionary<Guid, Layer> layers = new() { [Layer.DefaultLayerId] = Layer.Default };
    private readonly Dictionary<Guid, List<Operation>> changes = [];
    private readonly HashSet<Guid> tombstones = [];

    public bool Contains(Guid layerId) => layers.ContainsKey(layerId);
    public bool IsRemoved(Guid layerId) => tombstones.Contains(layerId);
    public ImmutableArray<Layer> GetLayers() => layers.Values.OrderBy(layer => layer.Position)
        .ThenBy(layer => layer.CreatedAt).ThenBy(layer => layer.LayerId).ToImmutableArray();
    public ImmutableArray<Guid> GetTombstones() => tombstones.Order().ToImmutableArray();
    public IEnumerable<Guid> GetPendingIds() => changes.Keys.Where(layerId => !layers.ContainsKey(layerId));

    public bool Apply(Operation operation)
    {
        Guid layerId;
        switch (operation)
        {
            case LayerAdd add:
                layerId = add.LayerId;
                break;
            case LayerRename rename:
                layerId = rename.LayerId;
                break;
            case LayerReorder reorder:
                layerId = reorder.LayerId;
                break;
            case LayerRemove remove:
                tombstones.Add(remove.LayerId);
                layers.Remove(remove.LayerId);
                changes.Remove(remove.LayerId);
                return true;
            default:
                return false;
        }
        if (tombstones.Contains(layerId))
        {
            return true;
        }
        if (!changes.TryGetValue(layerId, out var operations))
        {
            operations = [];
            changes.Add(layerId, operations);
        }
        operations.Add(operation);
        var ordered = operations.OrderBy(change => change.Stamp).ToArray();
        Layer? layer = null;
        if (layerId == Layer.DefaultLayerId)
        {
            layer = Layer.Default;
        }
        foreach (var change in ordered)
        {
            if (change is LayerAdd add && layer is null)
            {
                layer = new Layer(layerId, add.Name, add.Position, add.Stamp);
            }
        }
        if (layer is null)
        {
            return true;
        }
        foreach (var change in ordered)
        {
            if (layer.CreatedAt is not null && change.Stamp.CompareTo(layer.CreatedAt.Value) <= 0)
            {
                continue;
            }
            if (change is LayerRename rename)
            {
                layer = layer with { Name = rename.Name, NameChangedAt = rename.Stamp };
            }
            else if (change is LayerReorder reorder)
            {
                layer = layer with { Position = reorder.Position, OrderChangedAt = reorder.Stamp };
            }
        }
        layers[layerId] = layer;
        return true;
    }
}
