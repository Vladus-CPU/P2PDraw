using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace P2PDraw.Core;

public readonly record struct OperationStamp(Guid OperationId, Guid PeerId, long SequenceNumber, long LamportTime) : IComparable<OperationStamp>
{
    public int CompareTo(OperationStamp other)
    {
        int result = LamportTime.CompareTo(other.LamportTime);
        if (result == 0) result = PeerId.CompareTo(other.PeerId);
        if (result == 0) result = OperationId.CompareTo(other.OperationId);
        return result;
    }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(StrokeBegin), "strokeBegin")]
[JsonDerivedType(typeof(StrokeAppend), "strokeAppend")]
[JsonDerivedType(typeof(StrokeEnd), "strokeEnd")]
[JsonDerivedType(typeof(StrokeRemove), "strokeRemove")]
[JsonDerivedType(typeof(CanvasClear), "canvasClear")]
public abstract record Operation(OperationStamp Stamp)
{
    public void Validate()
    {
        if (Stamp.OperationId == Guid.Empty || Stamp.PeerId == Guid.Empty || Stamp.SequenceNumber <= 0 || Stamp.LamportTime <= 0)
            throw new ArgumentException("Invalid operation stamp.");

        bool valid = this switch
        {
            StrokeBegin begin => begin.StrokeId != Guid.Empty && double.IsFinite(begin.Width) && begin.Width > 0,
            StrokeAppend append => append.StrokeId != Guid.Empty && append.StartIndex >= 0 && !append.Points.IsDefaultOrEmpty
                && append.StartIndex <= int.MaxValue - append.Points.Length && append.Points.All(p => double.IsFinite(p.X) && double.IsFinite(p.Y)),
            StrokeEnd end => end.StrokeId != Guid.Empty && end.PointCount >= 0,
            StrokeRemove remove => remove.StrokeId != Guid.Empty,
            CanvasClear clear => !clear.StrokeIds.IsDefault && clear.StrokeIds.All(id => id != Guid.Empty),
            _ => false
        };
        if (!valid) throw new ArgumentException("Invalid operation payload.");
    }
}

public sealed record StrokeBegin(OperationStamp Stamp, Guid StrokeId, uint Color, double Width) : Operation(Stamp);
public sealed record StrokeAppend(OperationStamp Stamp, Guid StrokeId, int StartIndex, ImmutableArray<StrokePoint> Points) : Operation(Stamp);
public sealed record StrokeEnd(OperationStamp Stamp, Guid StrokeId, int PointCount) : Operation(Stamp);
public sealed record StrokeRemove(OperationStamp Stamp, Guid StrokeId) : Operation(Stamp);
public sealed record CanvasClear(OperationStamp Stamp, ImmutableArray<Guid> StrokeIds) : Operation(Stamp);
