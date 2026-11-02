using System.Collections.Immutable;
using P2PDraw.Core.Models;
using P2PDraw.Core.Operations;

namespace P2PDraw.Core.Sync;

public sealed class SyncEngine
{
    private readonly object stateLock = new();
    private readonly LamportClock clock = new();
    private DocumentState document = new();
    private long sequenceNumber;

    public Guid PeerId { get; }
    public Guid SessionId { get; }
    public long LamportTime
    {
        get
        {
            lock (stateLock)
            {
                return clock.Value;
            }
        }
    }

    public SyncEngine(Guid peerId, Guid sessionId)
    {
        if (peerId == Guid.Empty || sessionId == Guid.Empty)
        {
            throw new ArgumentException("Peer and session IDs are required.");
        }

        PeerId = peerId;
        SessionId = sessionId;
    }

    public ImmutableArray<Stroke> GetStrokes()
    {
        lock (stateLock)
        {
            return document.GetStrokes();
        }
    }

    public ImmutableArray<Guid> GetTombstones()
    {
        lock (stateLock)
        {
            return document.GetTombstones();
        }
    }

    public ImmutableArray<Shape> GetShapes()
    {
        lock (stateLock)
        {
            return document.GetShapes();
        }
    }

    public ImmutableArray<Layer> GetLayers()
    {
        lock (stateLock)
        {
            return document.GetLayers();
        }
    }

    public ImmutableArray<Guid> GetPendingLayerIds()
    {
        lock (stateLock)
        {
            return document.GetPendingLayerIds();
        }
    }

    public ImmutableArray<Guid> GetPendingStrokeIds()
    {
        lock (stateLock)
        {
            return document.GetPendingStrokeIds();
        }
    }

    public ImmutableArray<Operation> GetOperations()
    {
        lock (stateLock)
        {
            return document.GetOperations();
        }
    }

    public ImmutableArray<Operation> GetMissingOperations(IEnumerable<Guid> knownIds)
    {
        lock (stateLock)
        {
            return document.GetMissingOperations(knownIds);
        }
    }

    public Snapshot CreateSnapshot()
    {
        lock (stateLock)
        {
            var strokes = document.GetStrokes();
            var tombstones = document.GetTombstones();
            var operations = document.GetOperations();
            var pendingStrokeIds = document.GetPendingStrokeIds();
            return new Snapshot(SessionId, clock.Value, strokes, tombstones, operations, pendingStrokeIds)
            {
                Shapes = document.GetShapes(),
                Layers = document.GetLayers(),
                LayerTombstones = document.GetLayerTombstones(),
                PendingLayerIds = document.GetPendingLayerIds()
            };
        }
    }

    public void MergeSnapshot(Snapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.SessionId != SessionId)
        {
            throw new ArgumentException("Snapshot belongs to another session.");
        }

        snapshot.Validate();
        lock (stateLock)
        {
            var merged = new DocumentState();
            foreach (var operation in document.GetOperations())
            {
                merged.Apply(operation);
            }

            bool hasNewOperations = false;
            long restoredSequence = sequenceNumber;
            foreach (var operation in snapshot.Operations)
            {
                if (merged.Apply(operation))
                {
                    hasNewOperations = true;
                }

                if (operation.Stamp.PeerId == PeerId)
                {
                    restoredSequence = Math.Max(restoredSequence, operation.Stamp.SequenceNumber);
                }
            }
            if (hasNewOperations)
            {
                if (clock.Value == long.MaxValue)
                {
                    throw new ArgumentException("Logical time is exhausted.");
                }

                clock.Receive(snapshot.LamportTime);
            }
            sequenceNumber = restoredSequence;
            document = merged;
        }
    }

    public StrokeBegin BeginStroke(uint color, double width, Guid? layerId = null)
    {
        if (!double.IsFinite(width) || width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        lock (stateLock)
        {
            Guid targetLayer = layerId ?? Layer.DefaultLayerId;
            RequireLayer(targetLayer);
            var operation = new StrokeBegin(NextStamp(), Guid.NewGuid(), color, width) { LayerId = targetLayer };
            document.Apply(operation);
            return operation;
        }
    }

    public StrokeAppend AppendStroke(Guid strokeId, IEnumerable<StrokePoint> points)
    {
        var batch = points.ToImmutableArray();
        if (batch.IsEmpty || batch.Any(point => !double.IsFinite(point.X) || !double.IsFinite(point.Y)))
        {
            throw new ArgumentException("A batch must contain finite points.");
        }

        lock (stateLock)
        {
            var stroke = GetEditableStroke(strokeId);
            if (batch.Length > int.MaxValue - stroke.Points.Length)
            {
                throw new ArgumentException("Too many stroke points.");
            }

            var operation = new StrokeAppend(NextStamp(), strokeId, stroke.Points.Length, batch);
            document.Apply(operation);
            return operation;
        }
    }

    public ShapeAdd AddShape(ShapeKind kind, StrokePoint start, StrokePoint end, uint color, double width,
        uint? fill = null, Guid? layerId = null)
    {
        if (!Enum.IsDefined(kind) || !double.IsFinite(width) || width <= 0
            || !double.IsFinite(start.X) || !double.IsFinite(start.Y) || !double.IsFinite(end.X) || !double.IsFinite(end.Y))
        {
            throw new ArgumentException("Shape geometry and width must be valid finite values.");
        }
        lock (stateLock)
        {
            Guid targetLayer = layerId ?? Layer.DefaultLayerId;
            RequireLayer(targetLayer);
            var operation = new ShapeAdd(NextStamp(), Guid.NewGuid(), kind, start, end, color, width, fill) { LayerId = targetLayer };
            document.Apply(operation);
            return operation;
        }
    }

    public StrokeEnd EndStroke(Guid strokeId)
    {
        lock (stateLock)
        {
            var stroke = GetEditableStroke(strokeId);
            var operation = new StrokeEnd(NextStamp(), strokeId, stroke.Points.Length);
            document.Apply(operation);
            return operation;
        }
    }

    public StrokeRemove RemoveStroke(Guid strokeId)
    {
        if (strokeId == Guid.Empty)
        {
            throw new ArgumentException("Stroke ID is required.");
        }

        lock (stateLock)
        {
            var operation = new StrokeRemove(NextStamp(), strokeId);
            document.Apply(operation);
            return operation;
        }
    }

    public CanvasClear ClearCanvas()
    {
        lock (stateLock)
        {
            var operation = new CanvasClear(NextStamp(), document.GetKnownObjectIds());
            document.Apply(operation);
            return operation;
        }
    }

    public ObjectMove MoveObject(Guid objectId, StrokePoint offset)
    {
        if (objectId == Guid.Empty || !double.IsFinite(offset.X) || !double.IsFinite(offset.Y))
        {
            throw new ArgumentException("Object ID and finite offset are required.");
        }
        lock (stateLock)
        {
            if (!document.ContainsObject(objectId))
            {
                throw new InvalidOperationException("Object is missing or removed.");
            }
            var operation = new ObjectMove(NextStamp(), objectId, offset);
            document.Apply(operation);
            return operation;
        }
    }

    public ObjectRemove RemoveObject(Guid objectId)
    {
        if (objectId == Guid.Empty)
        {
            throw new ArgumentException("Object ID is required.");
        }
        lock (stateLock)
        {
            var operation = new ObjectRemove(NextStamp(), objectId);
            document.Apply(operation);
            return operation;
        }
    }

    public LayerAdd AddLayer(string name, double? position = null)
    {
        ArgumentNullException.ThrowIfNull(name);
        name = name.Trim();
        if (!Layer.IsValidName(name))
        {
            throw new ArgumentException("Layer name must contain 1 to 100 characters.");
        }
        lock (stateLock)
        {
            var layers = document.GetLayers();
            double targetPosition = position ?? 0;
            if (position is null && !layers.IsEmpty)
            {
                targetPosition = layers[^1].Position + 1;
            }
            if (!double.IsFinite(targetPosition))
            {
                throw new ArgumentException("Layer position must be finite.");
            }
            var operation = new LayerAdd(NextStamp(), Guid.NewGuid(), name, targetPosition);
            document.Apply(operation);
            return operation;
        }
    }

    public LayerRename RenameLayer(Guid layerId, string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        name = name.Trim();
        if (!Layer.IsValidName(name))
        {
            throw new ArgumentException("Layer name must contain 1 to 100 characters.");
        }
        lock (stateLock)
        {
            RequireLayer(layerId);
            var operation = new LayerRename(NextStamp(), layerId, name);
            document.Apply(operation);
            return operation;
        }
    }

    public LayerReorder SetLayerPosition(Guid layerId, double position)
    {
        if (!double.IsFinite(position))
        {
            throw new ArgumentException("Layer position must be finite.");
        }
        lock (stateLock)
        {
            RequireLayer(layerId);
            var operation = new LayerReorder(NextStamp(), layerId, position);
            document.Apply(operation);
            return operation;
        }
    }

    public LayerRemove RemoveLayer(Guid layerId)
    {
        if (layerId == Guid.Empty)
        {
            throw new ArgumentException("Layer ID is required.");
        }
        lock (stateLock)
        {
            var operation = new LayerRemove(NextStamp(), layerId);
            document.Apply(operation);
            return operation;
        }
    }

    public ObjectLayer MoveObjectToLayer(Guid objectId, Guid layerId)
    {
        lock (stateLock)
        {
            RequireLayer(layerId);
            if (!document.ContainsKnownObject(objectId))
            {
                throw new InvalidOperationException("Object is missing or removed.");
            }
            var operation = new ObjectLayer(NextStamp(), objectId, layerId);
            document.Apply(operation);
            return operation;
        }
    }

    public CanvasClear ClearLayer(Guid layerId)
    {
        lock (stateLock)
        {
            RequireLayer(layerId);
            var operation = new CanvasClear(NextStamp(), document.GetKnownObjectIds(layerId));
            document.Apply(operation);
            return operation;
        }
    }

    public bool Receive(Operation operation)
    {
        operation.Validate();
        lock (stateLock)
        {
            if (document.ContainsOperation(operation.Stamp.OperationId))
            {
                return false;
            }

            if (operation.Stamp.LamportTime == long.MaxValue || clock.Value == long.MaxValue)
            {
                throw new ArgumentException("Logical time is exhausted.");
            }

            if (!document.Apply(operation))
            {
                return false;
            }

            clock.Receive(operation.Stamp.LamportTime);
            if (operation.Stamp.PeerId == PeerId)
            {
                sequenceNumber = Math.Max(sequenceNumber, operation.Stamp.SequenceNumber);
            }

            return true;
        }
    }

    private OperationStamp NextStamp()
    {
        long nextSequence = checked(sequenceNumber + 1);
        long time = clock.Tick();
        sequenceNumber = nextSequence;
        return new(Guid.NewGuid(), PeerId, sequenceNumber, time);
    }

    private Stroke GetEditableStroke(Guid strokeId)
    {
        var stroke = document.GetStrokes().FirstOrDefault(stroke => stroke.StrokeId == strokeId);
        if (stroke is null || stroke.PeerId != PeerId || stroke.IsComplete || document.GetPendingStrokeIds().Contains(strokeId))
        {
            throw new InvalidOperationException("Stroke cannot be edited by this peer.");
        }

        return stroke;
    }

    private void RequireLayer(Guid layerId)
    {
        if (!document.ContainsLayer(layerId))
        {
            throw new InvalidOperationException("Layer is missing or removed.");
        }
    }
}
