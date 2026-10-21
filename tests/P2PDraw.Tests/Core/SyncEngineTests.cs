using P2PDraw.Core.Operations;
using P2PDraw.Core.Sync;
using System.Text.Json;
using Xunit;

namespace P2PDraw.Tests.Core;

public class SyncEngineTests
{
    [Fact]
    public void LocalDrawingIsImmediateAndOperationsAreUnique()
    {
        var engine = new SyncEngine(Guid.NewGuid(), Guid.NewGuid());
        var begin = engine.BeginStroke(0xFF112233, 2);
        Assert.Equal(begin.StrokeId, Assert.Single(engine.GetStrokes()).StrokeId);
        engine.AppendStroke(begin.StrokeId, [new(1, 2), new(3, 4)]);
        engine.EndStroke(begin.StrokeId);
        Assert.True(Assert.Single(engine.GetStrokes()).IsComplete);
        Assert.Equal(2, Assert.Single(engine.GetStrokes()).Points.Length);
        Assert.Equal(new long[] { 1, 2, 3 }, engine.GetOperations().Select(op => op.Stamp.SequenceNumber));
        Assert.Equal(3, engine.GetOperations().Select(op => op.Stamp.OperationId).Distinct().Count());
        Assert.Equal(3, engine.LamportTime);
    }

    [Fact]
    public void DuplicateDoesNotAdvanceClock()
    {
        var engine = new SyncEngine(Guid.NewGuid(), Guid.NewGuid());
        var operation = new StrokeBegin(new(Guid.NewGuid(), Guid.NewGuid(), 1, 20), Guid.NewGuid(), 0, 2);
        Assert.True(engine.Receive(operation));
        Assert.Equal(21, engine.LamportTime);
        Assert.False(engine.Receive(operation));
        Assert.Equal(21, engine.LamportTime);
        Assert.Equal(22, engine.BeginStroke(0, 1).Stamp.LamportTime);
    }

    [Fact]
    public void OfflineClearAndConcurrentDrawingConvergeAfterExchange()
    {
        var session = Guid.NewGuid();
        var a = new SyncEngine(Guid.NewGuid(), session);
        var b = new SyncEngine(Guid.NewGuid(), session);
        var first = a.BeginStroke(0, 2);
        a.AppendStroke(first.StrokeId, [new(0, 0)]);
        a.EndStroke(first.StrokeId);
        foreach (var operation in a.GetOperations()) b.Receive(operation);

        a.ClearCanvas();
        var concurrent = b.BeginStroke(0, 3);
        b.AppendStroke(concurrent.StrokeId, [new(2, 3)]);
        b.EndStroke(concurrent.StrokeId);
        var toA = b.GetMissingOperations(a.GetOperations().Select(op => op.Stamp.OperationId));
        var toB = a.GetMissingOperations(b.GetOperations().Select(op => op.Stamp.OperationId));
        foreach (var operation in toA.Reverse()) a.Receive(operation);
        foreach (var operation in toB.Reverse()) b.Receive(operation);

        Assert.Equal(concurrent.StrokeId, Assert.Single(a.GetStrokes()).StrokeId);
        Assert.Equal(JsonSerializer.Serialize(a.GetStrokes()), JsonSerializer.Serialize(b.GetStrokes()));
        Assert.Equal(a.GetTombstones(), b.GetTombstones());
        Assert.Equal(a.GetOperations(), b.GetOperations());
    }

    [Fact]
    public void InvalidLocalEditsDoNotConsumeSequenceNumbers()
    {
        var engine = new SyncEngine(Guid.NewGuid(), Guid.NewGuid());
        Assert.Throws<ArgumentOutOfRangeException>(() => engine.BeginStroke(0, 0));
        var stroke = engine.BeginStroke(0, 1);
        Assert.Throws<ArgumentException>(() => engine.AppendStroke(stroke.StrokeId, []));
        Assert.Throws<InvalidOperationException>(() => engine.AppendStroke(Guid.NewGuid(), [new(1, 1)]));
        Assert.Equal(2, engine.EndStroke(stroke.StrokeId).Stamp.SequenceNumber);
        Assert.Throws<InvalidOperationException>(() => engine.AppendStroke(stroke.StrokeId, [new(1, 1)]));
        Assert.Equal(2, engine.LamportTime);
    }

    [Fact]
    public async Task ConcurrentLocalCallsKeepUniqueSequences()
    {
        var engine = new SyncEngine(Guid.NewGuid(), Guid.NewGuid());
        await Task.WhenAll(Enumerable.Range(0, 100).Select(_ => Task.Run(() => engine.BeginStroke(0, 1))));
        Assert.Equal(100, engine.GetStrokes().Length);
        Assert.Equal(Enumerable.Range(1, 100).Select(i => (long)i), engine.GetOperations().Select(op => op.Stamp.SequenceNumber));
    }
}
