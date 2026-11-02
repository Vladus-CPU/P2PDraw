using System.Collections.Immutable;
using P2PDraw.Core.Operations;

namespace P2PDraw.Core.Models;

public sealed class DocumentState
{
    private readonly OperationLog log = new();
    private readonly Dictionary<Guid, List<Operation>> objectOperations = [];
    private readonly Dictionary<Guid, Stroke> strokes = [];
    private readonly Dictionary<Guid, Shape> shapes = [];
    // Object IDs are never reused: removing an ID is permanent, as in a two-phase set.
    private readonly HashSet<Guid> tombstones = [];
    private readonly HashSet<Guid> pendingStrokes = [];

    public int OperationCount => log.Count;
    public bool ContainsOperation(Guid operationId) => log.Contains(operationId);
    public bool ContainsObject(Guid objectId) => strokes.ContainsKey(objectId) || shapes.ContainsKey(objectId);
    public ImmutableArray<Operation> GetOperations() => log.GetOperations();
    public ImmutableArray<Operation> GetMissingOperations(IEnumerable<Guid> knownIds) => log.GetMissingOperations(knownIds);
    public ImmutableArray<Guid> GetTombstones() => tombstones.Order().ToImmutableArray();
    public ImmutableArray<Guid> GetPendingStrokeIds() => pendingStrokes.Order().ToImmutableArray();
    public ImmutableArray<Stroke> GetStrokes() => strokes.Values.OrderBy(stroke => stroke.CreatedAt).ToImmutableArray();
    public ImmutableArray<Shape> GetShapes() => shapes.Values.OrderBy(shape => shape.CreatedAt).ToImmutableArray();

    public bool Apply(Operation operation)
    {
        if (!log.Add(operation))
        {
            return false;
        }

        if (operation is CanvasClear clear)
        {
            foreach (var clearedObjectId in clear.StrokeIds)
            {
                Remove(clearedObjectId);
            }

            return true;
        }
        if (operation is StrokeRemove remove)
        {
            Remove(remove.StrokeId);
            return true;
        }
        if (operation is ObjectRemove objectRemove)
        {
            Remove(objectRemove.ObjectId);
            return true;
        }

        Guid objectId = operation switch
        {
            StrokeBegin begin => begin.StrokeId,
            StrokeAppend append => append.StrokeId,
            StrokeEnd end => end.StrokeId,
            ShapeAdd shape => shape.ShapeId,
            ObjectMove move => move.ObjectId,
            _ => throw new ArgumentException("Unknown drawing operation.")
        };
        if (tombstones.Contains(objectId))
        {
            return true;
        }

        if (!objectOperations.TryGetValue(objectId, out var changes))
        {
            changes = [];
            objectOperations.Add(objectId, changes);
        }
        changes.Add(operation);
        RebuildObject(objectId, changes);
        return true;
    }

    private void Remove(Guid objectId)
    {
        tombstones.Add(objectId);
        strokes.Remove(objectId);
        shapes.Remove(objectId);
        objectOperations.Remove(objectId);
        pendingStrokes.Remove(objectId);
    }

    private void RebuildObject(Guid objectId, List<Operation> changes)
    {
        var orderedOperations = changes.OrderBy(operation => operation.Stamp).ToArray();
        ObjectMove? latestMove = null;
        foreach (var operation in orderedOperations)
        {
            if (operation is ObjectMove move)
            {
                latestMove = move;
            }
        }
        // A colliding ID has one deterministic creation, even across different object kinds.
        foreach (var operation in orderedOperations)
        {
            if (operation is ShapeAdd shape)
            {
                strokes.Remove(objectId);
                pendingStrokes.Remove(objectId);
                shapes[objectId] = new Shape(objectId, shape.Stamp.PeerId, shape.Kind, shape.Start, shape.End,
                    shape.Color, shape.Width, shape.Stamp, shape.Fill)
                {
                    Offset = latestMove?.Offset ?? default,
                    MovedAt = latestMove?.Stamp
                };
                return;
            }
            if (operation is StrokeBegin begin)
            {
                shapes.Remove(objectId);
                RebuildStroke(objectId, orderedOperations, begin, latestMove);
                return;
            }
        }
        pendingStrokes.Add(objectId);
    }

    private void RebuildStroke(Guid strokeId, Operation[] orderedOperations, StrokeBegin begin, ObjectMove? latestMove)
    {
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
        strokes[strokeId] = new Stroke(strokeId, begin.Stamp.PeerId, begin.Color, begin.Width, begin.Stamp, contiguousPoints.ToImmutable(), isComplete)
        {
            Offset = latestMove?.Offset ?? default,
            MovedAt = latestMove?.Stamp
        };
    }
}
