using System.Collections.Immutable;
using System.Text.Json;
using P2PDraw.Core;
using Xunit;

namespace P2PDraw.Tests;

public class OperationTests
{
    [Fact]
    public void OrderUsesTimeThenPeerThenOperationId()
    {
        var peer = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var other = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var first = new OperationStamp(peer, peer, 9, 1);
        Assert.True(first.CompareTo(new OperationStamp(other, peer, 1, 1)) < 0);
        Assert.True(first.CompareTo(new OperationStamp(peer, other, 1, 1)) < 0);
        Assert.True(first.CompareTo(new OperationStamp(peer, peer, 1, 2)) < 0);
        Assert.Equal(0, first.CompareTo(first with { SequenceNumber = 10 }));
    }

    [Fact]
    public void AllOperationTypesSurviveJsonRoundTrip()
    {
        var stamp = new OperationStamp(Guid.NewGuid(), Guid.NewGuid(), 1, 1);
        var id = Guid.NewGuid();
        Operation[] operations = [new StrokeBegin(stamp, id, 0xFF112233, 2),
            new StrokeAppend(stamp, id, 0, [new(1, 2)]), new StrokeEnd(stamp, id, 1),
            new StrokeRemove(stamp, id), new CanvasClear(stamp, [id])];

        foreach (var operation in operations)
        {
            operation.Validate();
            string json = JsonSerializer.Serialize(operation);
            var restored = JsonSerializer.Deserialize<Operation>(json)!;
            restored.Validate();
            Assert.Equal(operation.GetType(), restored.GetType());
            Assert.Equal(json, JsonSerializer.Serialize(restored));
        }
    }

    [Fact]
    public void UnknownJsonTypeIsRejected()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Operation>("{\"type\":\"unknown\"}"));
    }

    [Fact]
    public void InvalidPayloadsAreRejected()
    {
        var stamp = new OperationStamp(Guid.NewGuid(), Guid.NewGuid(), 1, 1);
        var id = Guid.NewGuid();
        Operation[] operations = [new StrokeBegin(stamp, id, 0, double.NaN),
            new StrokeBegin(stamp, id, 0, 0), new StrokeEnd(stamp, id, -1),
            new StrokeRemove(stamp, Guid.Empty), new CanvasClear(stamp, default),
            new StrokeAppend(stamp, id, 0, ImmutableArray<StrokePoint>.Empty),
            new StrokeAppend(stamp, id, -1, [new(1, 1)]),
            new StrokeAppend(stamp, id, int.MaxValue, [new(1, 1)]),
            new StrokeAppend(stamp, id, 0, [new(double.PositiveInfinity, 1)]),
            new StrokeBegin(default, id, 0, 1)];

        foreach (var operation in operations) Assert.Throws<ArgumentException>(operation.Validate);
    }
}
