using P2PDraw.Core.Models;
using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace P2PDraw.Core.Operations;

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

        bool valid = false;
        switch (this)
        {
            case StrokeBegin begin:
                valid = begin.StrokeId != Guid.Empty && double.IsFinite(begin.Width) && begin.Width > 0;
                break;
            case StrokeAppend append:
                valid = append.StrokeId != Guid.Empty && append.StartIndex >= 0 && !append.Points.IsDefaultOrEmpty;
                if (!valid) break;
                valid = append.StartIndex <= int.MaxValue - append.Points.Length;
                if (!valid) break;
                foreach (var point in append.Points)
                {
                    if (!double.IsFinite(point.X) || !double.IsFinite(point.Y))
                    {
                        valid = false;
                        break;
                    }
                }
                break;
            case StrokeEnd end:
                valid = end.StrokeId != Guid.Empty && end.PointCount >= 0;
                break;
            case StrokeRemove remove:
                valid = remove.StrokeId != Guid.Empty;
                break;
            case CanvasClear clear:
                valid = !clear.StrokeIds.IsDefault;
                if (!valid) break;
                foreach (var strokeId in clear.StrokeIds)
                {
                    if (strokeId == Guid.Empty)
                    {
                        valid = false;
                        break;
                    }
                }
                break;
        }
        if (!valid) throw new ArgumentException("Invalid operation payload.");
    }
}

public sealed record StrokeBegin(OperationStamp Stamp, Guid StrokeId, uint Color, double Width) : Operation(Stamp);
public sealed record StrokeAppend(OperationStamp Stamp, Guid StrokeId, int StartIndex, ImmutableArray<StrokePoint> Points) : Operation(Stamp);
public sealed record StrokeEnd(OperationStamp Stamp, Guid StrokeId, int PointCount) : Operation(Stamp);
public sealed record StrokeRemove(OperationStamp Stamp, Guid StrokeId) : Operation(Stamp);
public sealed record CanvasClear(OperationStamp Stamp, ImmutableArray<Guid> StrokeIds) : Operation(Stamp);
