using System.Collections.Immutable;
using P2PDraw.Core.Operations;

namespace P2PDraw.Core.Models;

public readonly record struct StrokePoint(double X, double Y);

public sealed record Stroke(Guid StrokeId, Guid PeerId, uint Color, double Width,
    OperationStamp CreatedAt, ImmutableArray<StrokePoint> Points, bool IsComplete)
{
    public StrokePoint Offset { get; init; }
    public OperationStamp? MovedAt { get; init; }
}
