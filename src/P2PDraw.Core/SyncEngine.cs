using System.Collections.Immutable;

namespace P2PDraw.Core;

public sealed class SyncEngine
{
    private readonly object gate = new();
    private readonly LamportClock clock = new();
    private readonly DocumentState document = new();
    private long sequence;

    public Guid PeerId { get; }
    public Guid SessionId { get; }
    public long LamportTime { get { lock (gate) return clock.Value; } }

    public SyncEngine(Guid peerId, Guid sessionId)
    {
        if (peerId == Guid.Empty || sessionId == Guid.Empty) throw new ArgumentException("Peer and session IDs are required.");
        PeerId = peerId;
        SessionId = sessionId;
    }

    public ImmutableArray<Stroke> GetStrokes() { lock (gate) return document.GetStrokes(); }
    public ImmutableArray<Guid> GetTombstones() { lock (gate) return document.GetTombstones(); }
    public ImmutableArray<Guid> GetPendingStrokeIds() { lock (gate) return document.GetPendingStrokeIds(); }
    public ImmutableArray<Operation> GetOperations() { lock (gate) return document.GetOperations(); }
    public ImmutableArray<Operation> GetMissingOperations(IEnumerable<Guid> knownIds) { lock (gate) return document.GetMissingOperations(knownIds); }

    public StrokeBegin BeginStroke(uint color, double width)
    {
        if (!double.IsFinite(width) || width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        lock (gate)
        {
            var operation = new StrokeBegin(NextStamp(), Guid.NewGuid(), color, width);
            document.Apply(operation);
            return operation;
        }
    }

    public StrokeAppend AppendStroke(Guid strokeId, IEnumerable<StrokePoint> points)
    {
        var batch = points.ToImmutableArray();
        if (batch.IsEmpty || batch.Any(p => !double.IsFinite(p.X) || !double.IsFinite(p.Y)))
            throw new ArgumentException("A batch must contain finite points.");
        lock (gate)
        {
            var stroke = GetEditableStroke(strokeId);
            if (batch.Length > int.MaxValue - stroke.Points.Length) throw new ArgumentException("Too many stroke points.");
            var operation = new StrokeAppend(NextStamp(), strokeId, stroke.Points.Length, batch);
            document.Apply(operation);
            return operation;
        }
    }

    public StrokeEnd EndStroke(Guid strokeId)
    {
        lock (gate)
        {
            var stroke = GetEditableStroke(strokeId);
            var operation = new StrokeEnd(NextStamp(), strokeId, stroke.Points.Length);
            document.Apply(operation);
            return operation;
        }
    }

    public StrokeRemove RemoveStroke(Guid strokeId)
    {
        if (strokeId == Guid.Empty) throw new ArgumentException("Stroke ID is required.");
        lock (gate)
        {
            var operation = new StrokeRemove(NextStamp(), strokeId);
            document.Apply(operation);
            return operation;
        }
    }

    public CanvasClear ClearCanvas()
    {
        lock (gate)
        {
            var operation = new CanvasClear(NextStamp(), document.GetStrokes().Select(stroke => stroke.StrokeId).ToImmutableArray());
            document.Apply(operation);
            return operation;
        }
    }

    public bool Receive(Operation operation)
    {
        operation.Validate();
        lock (gate)
        {
            if (document.ContainsOperation(operation.Stamp.OperationId)) return false;
            if (operation.Stamp.LamportTime == long.MaxValue || clock.Value == long.MaxValue) throw new ArgumentException("Logical time is exhausted.");
            if (!document.Apply(operation)) return false;
            clock.Receive(operation.Stamp.LamportTime);
            if (operation.Stamp.PeerId == PeerId) sequence = Math.Max(sequence, operation.Stamp.SequenceNumber);
            return true;
        }
    }

    private OperationStamp NextStamp()
    {
        long nextSequence = checked(sequence + 1);
        long time = clock.Tick();
        sequence = nextSequence;
        return new(Guid.NewGuid(), PeerId, sequence, time);
    }

    private Stroke GetEditableStroke(Guid id)
    {
        var stroke = document.GetStrokes().FirstOrDefault(stroke => stroke.StrokeId == id);
        if (stroke is null || stroke.PeerId != PeerId || stroke.IsComplete || document.GetPendingStrokeIds().Contains(id))
            throw new InvalidOperationException("Stroke cannot be edited by this peer.");
        return stroke;
    }
}
