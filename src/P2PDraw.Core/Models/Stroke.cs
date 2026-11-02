using System.Collections.Immutable;
using System.Text.Json.Serialization;
using P2PDraw.Core.Operations;

namespace P2PDraw.Core.Models;

[method: JsonConstructor]
public readonly record struct StrokePoint(double X, double Y);

public sealed record Stroke(Guid StrokeId, Guid PeerId, uint Color, double Width,
    OperationStamp CreatedAt, ImmutableArray<StrokePoint> Points, bool IsComplete)
{
    public StrokePoint Offset { get; init; }
    public OperationStamp? MovedAt { get; init; }
    public Guid LayerId { get; init; } = Layer.DefaultLayerId;
    public OperationStamp? LayerChangedAt { get; init; }
}
