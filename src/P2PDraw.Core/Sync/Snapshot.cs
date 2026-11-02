using System.Collections.Immutable;
using System.Text.Json;
using P2PDraw.Core.Models;
using P2PDraw.Core.Operations;

namespace P2PDraw.Core.Sync;

// The full log also preserves point batches that are waiting for missing data.
public sealed record Snapshot(Guid SessionId, long LamportTime, ImmutableArray<Stroke> Strokes,
    ImmutableArray<Guid> Tombstones, ImmutableArray<Operation> Operations, ImmutableArray<Guid> PendingStrokeIds)
{
    public ImmutableArray<Shape> Shapes { get; init; } = [];
    public ImmutableArray<Layer> Layers { get; init; } = [Layer.Default];
    public ImmutableArray<Guid> LayerTombstones { get; init; } = [];
    public ImmutableArray<Guid> PendingLayerIds { get; init; } = [];

    internal void Validate()
    {
        if (SessionId == Guid.Empty || LamportTime < 0 || LamportTime == long.MaxValue
            || Strokes.IsDefault || Shapes.IsDefault || Layers.IsDefault || LayerTombstones.IsDefault
            || PendingLayerIds.IsDefault || Tombstones.IsDefault || Operations.IsDefault || PendingStrokeIds.IsDefault)
        {
            throw new ArgumentException("Invalid snapshot metadata.");
        }

        if (Strokes.Any(stroke => stroke is null || stroke.Points.IsDefault))
        {
            throw new ArgumentException("Invalid snapshot stroke.");
        }
        if (Shapes.Any(shape => shape is null))
        {
            throw new ArgumentException("Invalid snapshot shape.");
        }

        var restoredDocument = new DocumentState();
        foreach (var operation in Operations)
        {
            if (operation is null || operation.Stamp.LamportTime > LamportTime)
            {
                throw new ArgumentException("Invalid snapshot operation.");
            }

            if (!restoredDocument.Apply(operation))
            {
                throw new ArgumentException("Duplicate snapshot operation.");
            }
        }
        if (!restoredDocument.GetTombstones().SequenceEqual(Tombstones) || !restoredDocument.GetPendingStrokeIds().SequenceEqual(PendingStrokeIds))
        {
            throw new ArgumentException("Snapshot state does not match its log.");
        }

        string restoredStrokesJson = JsonSerializer.Serialize(restoredDocument.GetStrokes());
        string snapshotStrokesJson = JsonSerializer.Serialize(Strokes);
        if (restoredStrokesJson != snapshotStrokesJson)
        {
            throw new ArgumentException("Snapshot state does not match its log.");
        }
        if (JsonSerializer.Serialize(restoredDocument.GetShapes()) != JsonSerializer.Serialize(Shapes))
        {
            throw new ArgumentException("Snapshot shapes do not match its log.");
        }
        if (JsonSerializer.Serialize(restoredDocument.GetLayers()) != JsonSerializer.Serialize(Layers)
            || !restoredDocument.GetLayerTombstones().SequenceEqual(LayerTombstones)
            || !restoredDocument.GetPendingLayerIds().SequenceEqual(PendingLayerIds))
        {
            throw new ArgumentException("Snapshot layers do not match its log.");
        }
    }
}
