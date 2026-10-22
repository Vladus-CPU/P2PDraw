using System.Text.Json;
using P2PDraw.Core.Models;
using P2PDraw.Core.Operations;
using Xunit;

namespace P2PDraw.Tests.Core;

public class DocumentStateTests
{
    private static Operation[] CreateStroke(Guid peerId, Guid strokeId, long firstSequence = 1) =>
    [
        new StrokeBegin(new(Guid.NewGuid(), peerId, firstSequence, 1), strokeId, 0xFF112233, 3),
        new StrokeAppend(new(Guid.NewGuid(), peerId, firstSequence + 1, 2), strokeId, 0, [new(1, 2), new(3, 4)]),
        new StrokeAppend(new(Guid.NewGuid(), peerId, firstSequence + 2, 3), strokeId, 2, [new(5, 6)]),
        new StrokeEnd(new(Guid.NewGuid(), peerId, firstSequence + 3, 4), strokeId, 3)
    ];

    private static string Normalize(DocumentState document) => JsonSerializer.Serialize(new
    {
        Strokes = document.GetStrokes(),
        Tombstones = document.GetTombstones(),
        Pending = document.GetPendingStrokeIds()
    });

    [Fact]
    public void DifferentDeliveryOrdersAndDuplicatesConverge()
    {
        Operation[] operations = [.. CreateStroke(Guid.NewGuid(), Guid.NewGuid()), .. CreateStroke(Guid.NewGuid(), Guid.NewGuid())];
        var expected = new DocumentState();
        foreach (var operation in operations)
        {
            expected.Apply(operation);
        }

        for (int seed = 0; seed < 50; seed++)
        {
            var actual = new DocumentState();
            var random = new Random(seed);
            var duplicatedOperations = operations.Concat(operations).ToArray();
            var deliveryOrder = duplicatedOperations.OrderBy(_ => random.Next());
            foreach (var operation in deliveryOrder)
            {
                actual.Apply(operation);
            }

            Assert.Equal(Normalize(expected), Normalize(actual));
            Assert.Equal(operations.Length, actual.OperationCount);
            Assert.All(actual.GetStrokes(), stroke => Assert.True(stroke.IsComplete));
        }
    }

    [Fact]
    public void AppendAndEndWaitForBeginAndMissingPoints()
    {
        var strokeId = Guid.NewGuid();
        var operations = CreateStroke(Guid.NewGuid(), strokeId);
        var document = new DocumentState();
        document.Apply(operations[2]);
        document.Apply(operations[3]);
        Assert.Empty(document.GetStrokes());
        Assert.Equal(strokeId, Assert.Single(document.GetPendingStrokeIds()));

        document.Apply(operations[0]);
        Assert.Empty(Assert.Single(document.GetStrokes()).Points);
        Assert.False(Assert.Single(document.GetStrokes()).IsComplete);
        Assert.Single(document.GetPendingStrokeIds());

        document.Apply(operations[1]);
        Assert.Equal(3, Assert.Single(document.GetStrokes()).Points.Length);
        Assert.True(Assert.Single(document.GetStrokes()).IsComplete);
        Assert.Empty(document.GetPendingStrokeIds());
    }

    [Fact]
    public void RemovalSurvivesLateMessagesAndRepeatedRemoval()
    {
        var strokeId = Guid.NewGuid();
        var peerId = Guid.NewGuid();
        var operations = CreateStroke(peerId, strokeId);
        var remove = new StrokeRemove(new(Guid.NewGuid(), peerId, 5, 5), strokeId);
        var document = new DocumentState();
        document.Apply(operations[2]);
        document.Apply(remove);
        foreach (var operation in operations.Reverse())
        {
            document.Apply(operation);
        }

        document.Apply(remove with { Stamp = new(Guid.NewGuid(), peerId, 6, 6) });
        Assert.Empty(document.GetStrokes());
        Assert.Empty(document.GetPendingStrokeIds());
        Assert.Equal(strokeId, Assert.Single(document.GetTombstones()));
    }

    [Fact]
    public void ClearRemovesOnlyObservedStrokesInAnyDeliveryOrder()
    {
        var peerA = Guid.NewGuid();
        var peerB = Guid.NewGuid();
        var knownId = Guid.NewGuid();
        var concurrentId = Guid.NewGuid();
        var clear = new CanvasClear(new(Guid.NewGuid(), peerA, 5, 5), [knownId]);
        Operation[] operations = [.. CreateStroke(peerA, knownId), clear, .. CreateStroke(peerB, concurrentId)];

        for (int seed = 0; seed < 50; seed++)
        {
            var document = new DocumentState();
            var random = new Random(seed);
            var deliveryOrder = operations.OrderBy(_ => random.Next());
            foreach (var operation in deliveryOrder)
            {
                document.Apply(operation);
            }

            var survivingStroke = Assert.Single(document.GetStrokes());
            Assert.Equal(concurrentId, survivingStroke.StrokeId);
            Assert.True(survivingStroke.IsComplete);
            Assert.Equal(knownId, Assert.Single(document.GetTombstones()));
            Assert.Empty(document.GetPendingStrokeIds());
        }
    }

    [Fact]
    public void ForeignPeerCannotAppendOrEndAnotherAuthorsStroke()
    {
        var document = new DocumentState();
        var strokeId = Guid.NewGuid();
        var peerId = Guid.NewGuid();
        document.Apply(new StrokeBegin(new(Guid.NewGuid(), peerId, 1, 1), strokeId, 0, 1));
        document.Apply(new StrokeAppend(new(Guid.NewGuid(), Guid.NewGuid(), 1, 2), strokeId, 0, [new(9, 9)]));
        document.Apply(new StrokeEnd(new(Guid.NewGuid(), Guid.NewGuid(), 1, 3), strokeId, 0));
        var stroke = Assert.Single(document.GetStrokes());
        Assert.Empty(stroke.Points);
        Assert.False(stroke.IsComplete);
    }
}
