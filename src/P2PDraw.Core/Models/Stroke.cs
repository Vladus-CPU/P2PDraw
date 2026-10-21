using P2PDraw.Core.Operations;
using System.Collections.Immutable;

namespace P2PDraw.Core.Models;

public readonly record struct StrokePoint(double X, double Y);

public sealed record Stroke(Guid StrokeId, Guid PeerId, uint Color, double Width, OperationStamp CreatedAt, ImmutableArray<StrokePoint> Points, bool IsComplete);
