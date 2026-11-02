using P2PDraw.Core.Operations;

namespace P2PDraw.Core.Models;

public enum ShapeKind
{
    Line,
    Rectangle,
    Ellipse
}

public sealed record Shape(Guid ShapeId, Guid PeerId, ShapeKind Kind, StrokePoint Start, StrokePoint End,
    uint Color, double Width, OperationStamp CreatedAt, uint? Fill = null)
{
    public StrokePoint Offset { get; init; }
    public OperationStamp? MovedAt { get; init; }
    public Guid LayerId { get; init; } = Layer.DefaultLayerId;
    public OperationStamp? LayerChangedAt { get; init; }
}
