using P2PDraw.Core.Operations;
using System.Collections.Immutable;

namespace P2PDraw.Core.Models;

public sealed class DocumentState
{
    private readonly OperationLog log = new();
    private readonly Dictionary<Guid, List<Operation>> strokeOperations = [];
    private readonly Dictionary<Guid, Stroke> strokes = [];
    private readonly HashSet<Guid> tombstones = [];
    private readonly HashSet<Guid> pendingStrokes = [];

    public int OperationCount => log.Count;
    public bool ContainsOperation(Guid id) => log.Contains(id);
    public ImmutableArray<Operation> GetOperations() => log.GetOperations();
    public ImmutableArray<Operation> GetMissingOperations(IEnumerable<Guid> knownIds) => log.GetMissingOperations(knownIds);
    public ImmutableArray<Guid> GetTombstones() => tombstones.Order().ToImmutableArray();
    public ImmutableArray<Guid> GetPendingStrokeIds() => pendingStrokes.Order().ToImmutableArray();
    public ImmutableArray<Stroke> GetStrokes() => strokes.Values.OrderBy(stroke => stroke.CreatedAt).ToImmutableArray();

    public bool Apply(Operation operation)
    {
        if (!log.Add(operation)) return false;

        if (operation is CanvasClear clear)
        {
            foreach (var id in clear.StrokeIds) Remove(id);
            return true;
        }
        if (operation is StrokeRemove remove)
        {
            Remove(remove.StrokeId);
            return true;
        }

        Guid strokeId = operation switch
        {
            StrokeBegin begin => begin.StrokeId,
            StrokeAppend append => append.StrokeId,
            StrokeEnd end => end.StrokeId,
            _ => throw new ArgumentException("Unknown stroke operation.")
        };
        if (tombstones.Contains(strokeId)) return true;
        if (!strokeOperations.TryGetValue(strokeId, out var changes))
        {
            changes = [];
            strokeOperations.Add(strokeId, changes);
        }
        changes.Add(operation);
        RebuildStroke(strokeId, changes);
        return true;
    }

    private void Remove(Guid id)
    {
        tombstones.Add(id);
        strokes.Remove(id);
        strokeOperations.Remove(id);
        pendingStrokes.Remove(id);
    }

    private void RebuildStroke(Guid id, List<Operation> changes)
    {
        var ordered = changes.OrderBy(op => op.Stamp).ToArray();
        var begin = ordered.OfType<StrokeBegin>().FirstOrDefault();
        if (begin is null)
        {
            pendingStrokes.Add(id);
            return;
        }

        var points = new SortedDictionary<int, StrokePoint>();
        foreach (var append in ordered.OfType<StrokeAppend>().Where(op => op.Stamp.PeerId == begin.Stamp.PeerId))
        {
            for (int i = 0; i < append.Points.Length; i++) points.TryAdd(append.StartIndex + i, append.Points[i]);
        }
        var contiguous = ImmutableArray.CreateBuilder<StrokePoint>();
        while (points.TryGetValue(contiguous.Count, out var point)) contiguous.Add(point);

        var end = ordered.OfType<StrokeEnd>().FirstOrDefault(op => op.Stamp.PeerId == begin.Stamp.PeerId);
        bool hasGap = contiguous.Count != points.Count || (end is not null && contiguous.Count != end.PointCount);
        if (hasGap) pendingStrokes.Add(id);
        else pendingStrokes.Remove(id);

        strokes[id] = new Stroke(id, begin.Stamp.PeerId, begin.Color, begin.Width, begin.Stamp, contiguous.ToImmutable(), end is not null && !hasGap);
    }
}
