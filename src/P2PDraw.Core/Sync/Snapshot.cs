using P2PDraw.Core.Models;
using P2PDraw.Core.Operations;
using System.Collections.Immutable;
using System.Text.Json;

namespace P2PDraw.Core.Sync;

// The full log also preserves point batches that are waiting for missing data.
public sealed record Snapshot(Guid SessionId, long LamportTime, ImmutableArray<Stroke> Strokes, ImmutableArray<Guid> Tombstones, ImmutableArray<Operation> Operations, ImmutableArray<Guid> PendingStrokeIds)
{
    internal void Validate()
    {
        if (SessionId == Guid.Empty || LamportTime < 0 || LamportTime == long.MaxValue || Strokes.IsDefault || Tombstones.IsDefault || Operations.IsDefault || PendingStrokeIds.IsDefault)
            throw new ArgumentException("Invalid snapshot metadata.");
        if (Strokes.Any(stroke => stroke is null || stroke.Points.IsDefault)) throw new ArgumentException("Invalid snapshot stroke.");

        var restored = new DocumentState();
        foreach (var operation in Operations)
        {
            if (operation is null || operation.Stamp.LamportTime > LamportTime) throw new ArgumentException("Invalid snapshot operation.");
            if (!restored.Apply(operation)) throw new ArgumentException("Duplicate snapshot operation.");
        }
        if (!restored.GetTombstones().SequenceEqual(Tombstones) || !restored.GetPendingStrokeIds().SequenceEqual(PendingStrokeIds)
            || JsonSerializer.Serialize(restored.GetStrokes()) != JsonSerializer.Serialize(Strokes))
            throw new ArgumentException("Snapshot state does not match its log.");
    }
}
