using System.Collections.Immutable;
using P2PDraw.Core.Operations;

namespace P2PDraw.Core.Models;

public sealed class DocumentState
{
    private readonly OperationLog log = new();
    private readonly Dictionary<Guid, List<Operation>> strokeOperations = [];
    private readonly Dictionary<Guid, Stroke> strokes = [];
    private readonly HashSet<Guid> tombstones = [];
    private readonly HashSet<Guid> pendingStrokes = [];

    public int OperationCount => log.Count;
    public bool ContainsOperation(Guid operationId) => log.Contains(operationId);
    public ImmutableArray<Operation> GetOperations() => log.GetOperations();
    public ImmutableArray<Operation> GetMissingOperations(IEnumerable<Guid> knownIds) => log.GetMissingOperations(knownIds);
    public ImmutableArray<Guid> GetTombstones() => tombstones.Order().ToImmutableArray();
    public ImmutableArray<Guid> GetPendingStrokeIds() => pendingStrokes.Order().ToImmutableArray();
    public ImmutableArray<Stroke> GetStrokes() => strokes.Values.OrderBy(stroke => stroke.CreatedAt).ToImmutableArray();

    public bool Apply(Operation operation)
    {
        if (!log.Add(operation))
        {
            return false;
        }

        if (operation is CanvasClear clear)
        {
            foreach (var clearedStrokeId in clear.StrokeIds)
            {
                Remove(clearedStrokeId);
            }

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
        if (tombstones.Contains(strokeId))
        {
            return true;
        }

        if (!strokeOperations.TryGetValue(strokeId, out var changes))
        {
            changes = [];
            strokeOperations.Add(strokeId, changes);
        }
        changes.Add(operation);
        RebuildStroke(strokeId, changes);
        return true;
    }

    private void Remove(Guid strokeId)
    {
        tombstones.Add(strokeId);
        strokes.Remove(strokeId);
        strokeOperations.Remove(strokeId);
        pendingStrokes.Remove(strokeId);
    }

    private void RebuildStroke(Guid strokeId, List<Operation> changes)
    {
        var orderedOperations = changes.OrderBy(operation => operation.Stamp).ToArray();
        var begin = orderedOperations.OfType<StrokeBegin>().FirstOrDefault();
        if (begin is null)
        {
            pendingStrokes.Add(strokeId);
            return;
        }

        var points = new SortedDictionary<int, StrokePoint>();
        foreach (var operation in orderedOperations)
        {
            if (operation is not StrokeAppend append || append.Stamp.PeerId != begin.Stamp.PeerId)
            {
                continue;
            }

            for (int i = 0; i < append.Points.Length; i++)
            {
                points.TryAdd(append.StartIndex + i, append.Points[i]);
            }
        }
        var contiguousPoints = ImmutableArray.CreateBuilder<StrokePoint>();
        while (points.TryGetValue(contiguousPoints.Count, out var point))
        {
            contiguousPoints.Add(point);
        }

        StrokeEnd? end = null;
        foreach (var operation in orderedOperations)
        {
            if (operation is StrokeEnd strokeEnd && strokeEnd.Stamp.PeerId == begin.Stamp.PeerId)
            {
                end = strokeEnd;
                break;
            }
        }
        bool hasGap = contiguousPoints.Count != points.Count || (end is not null && contiguousPoints.Count != end.PointCount);
        if (hasGap)
        {
            pendingStrokes.Add(strokeId);
        }
        else
        {
            pendingStrokes.Remove(strokeId);
        }

        bool isComplete = end is not null && !hasGap;
        strokes[strokeId] = new Stroke(strokeId, begin.Stamp.PeerId, begin.Color, begin.Width, begin.Stamp, contiguousPoints.ToImmutable(), isComplete);
    }
}
