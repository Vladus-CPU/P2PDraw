using System.Collections.Immutable;
using System.Text.Json.Serialization;
using P2PDraw.Core.Models;

namespace P2PDraw.Core.Operations;

public readonly record struct OperationStamp(Guid OperationId, Guid PeerId, long SequenceNumber, long LamportTime) : IComparable<OperationStamp>
{
    public int CompareTo(OperationStamp other)
    {
        int result = LamportTime.CompareTo(other.LamportTime);
        if (result == 0)
        {
            result = PeerId.CompareTo(other.PeerId);
        }

        if (result == 0)
        {
            result = OperationId.CompareTo(other.OperationId);
        }

        return result;
    }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(StrokeBegin), "strokeBegin")]
[JsonDerivedType(typeof(StrokeAppend), "strokeAppend")]
[JsonDerivedType(typeof(StrokeEnd), "strokeEnd")]
[JsonDerivedType(typeof(StrokeRemove), "strokeRemove")]
[JsonDerivedType(typeof(CanvasClear), "canvasClear")]
[JsonDerivedType(typeof(ShapeAdd), "shapeAdd")]
[JsonDerivedType(typeof(ObjectMove), "objectMove")]
[JsonDerivedType(typeof(ObjectRemove), "objectRemove")]
[JsonDerivedType(typeof(LayerAdd), "layerAdd")]
[JsonDerivedType(typeof(LayerRename), "layerRename")]
[JsonDerivedType(typeof(LayerReorder), "layerReorder")]
[JsonDerivedType(typeof(LayerRemove), "layerRemove")]
[JsonDerivedType(typeof(ObjectLayer), "objectLayer")]
public abstract record Operation(OperationStamp Stamp)
{
    public void Validate()
    {
        if (Stamp.OperationId == Guid.Empty || Stamp.PeerId == Guid.Empty || Stamp.SequenceNumber <= 0 || Stamp.LamportTime <= 0)
        {
            throw new ArgumentException("Invalid operation stamp.");
        }

        bool isValid = false;
        switch (this)
        {
            case StrokeBegin begin:
                isValid = begin.StrokeId != Guid.Empty && begin.LayerId != Guid.Empty && double.IsFinite(begin.Width) && begin.Width > 0;
                break;
            case ShapeAdd shape:
                isValid = shape.ShapeId != Guid.Empty && shape.LayerId != Guid.Empty && Enum.IsDefined(shape.Kind)
                    && double.IsFinite(shape.Width) && shape.Width > 0
                    && double.IsFinite(shape.Start.X) && double.IsFinite(shape.Start.Y)
                    && double.IsFinite(shape.End.X) && double.IsFinite(shape.End.Y);
                break;
            case StrokeAppend append:
                isValid = append.StrokeId != Guid.Empty && append.StartIndex >= 0 && !append.Points.IsDefaultOrEmpty;
                if (!isValid)
                {
                    break;
                }

                isValid = append.StartIndex <= int.MaxValue - append.Points.Length;
                if (!isValid)
                {
                    break;
                }

                foreach (var point in append.Points)
                {
                    if (!double.IsFinite(point.X) || !double.IsFinite(point.Y))
                    {
                        isValid = false;
                        break;
                    }
                }
                break;
            case StrokeEnd end:
                isValid = end.StrokeId != Guid.Empty && end.PointCount >= 0;
                break;
            case StrokeRemove remove:
                isValid = remove.StrokeId != Guid.Empty;
                break;
            case ObjectMove move:
                isValid = move.ObjectId != Guid.Empty && double.IsFinite(move.Offset.X) && double.IsFinite(move.Offset.Y);
                break;
            case ObjectRemove remove:
                isValid = remove.ObjectId != Guid.Empty;
                break;
            case LayerAdd add:
                isValid = add.LayerId != Guid.Empty && add.LayerId != Layer.DefaultLayerId
                    && Layer.IsValidName(add.Name) && double.IsFinite(add.Position);
                break;
            case LayerRename rename:
                isValid = rename.LayerId != Guid.Empty && Layer.IsValidName(rename.Name);
                break;
            case LayerReorder reorder:
                isValid = reorder.LayerId != Guid.Empty && double.IsFinite(reorder.Position);
                break;
            case LayerRemove remove:
                isValid = remove.LayerId != Guid.Empty;
                break;
            case ObjectLayer membership:
                isValid = membership.ObjectId != Guid.Empty && membership.LayerId != Guid.Empty;
                break;
            case CanvasClear clear:
                isValid = !clear.StrokeIds.IsDefault;
                if (!isValid)
                {
                    break;
                }

                foreach (var strokeId in clear.StrokeIds)
                {
                    if (strokeId == Guid.Empty)
                    {
                        isValid = false;
                        break;
                    }
                }
                break;
        }
        if (!isValid)
        {
            throw new ArgumentException("Invalid operation payload.");
        }
    }
}

public sealed record StrokeBegin(OperationStamp Stamp, Guid StrokeId, uint Color, double Width) : Operation(Stamp)
{
    public Guid LayerId { get; init; } = Layer.DefaultLayerId;
}
public sealed record StrokeAppend(OperationStamp Stamp, Guid StrokeId, int StartIndex, ImmutableArray<StrokePoint> Points) : Operation(Stamp);
public sealed record StrokeEnd(OperationStamp Stamp, Guid StrokeId, int PointCount) : Operation(Stamp);
public sealed record StrokeRemove(OperationStamp Stamp, Guid StrokeId) : Operation(Stamp);
public sealed record CanvasClear(OperationStamp Stamp, ImmutableArray<Guid> StrokeIds) : Operation(Stamp);
public sealed record ShapeAdd(OperationStamp Stamp, Guid ShapeId, ShapeKind Kind, StrokePoint Start, StrokePoint End,
    uint Color, double Width, uint? Fill = null) : Operation(Stamp)
{
    public Guid LayerId { get; init; } = Layer.DefaultLayerId;
}
public sealed record ObjectMove(OperationStamp Stamp, Guid ObjectId, StrokePoint Offset) : Operation(Stamp);
public sealed record ObjectRemove(OperationStamp Stamp, Guid ObjectId) : Operation(Stamp);
