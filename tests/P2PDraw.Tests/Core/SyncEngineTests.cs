using System.Text.Json;
using P2PDraw.Core.Operations;
using P2PDraw.Core.Sync;
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
        Assert.Equal(new long[] { 1, 2, 3 }, engine.GetOperations().Select(operation => operation.Stamp.SequenceNumber));
        Assert.Equal(3, engine.GetOperations().Select(operation => operation.Stamp.OperationId).Distinct().Count());
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
        var firstEngine = new SyncEngine(Guid.NewGuid(), session);
        var secondEngine = new SyncEngine(Guid.NewGuid(), session);
        var first = firstEngine.BeginStroke(0, 2);
        firstEngine.AppendStroke(first.StrokeId, [new(0, 0)]);
        firstEngine.EndStroke(first.StrokeId);
        foreach (var operation in firstEngine.GetOperations())
        {
            secondEngine.Receive(operation);
        }

        firstEngine.ClearCanvas();
        var concurrent = secondEngine.BeginStroke(0, 3);
        secondEngine.AppendStroke(concurrent.StrokeId, [new(2, 3)]);
        secondEngine.EndStroke(concurrent.StrokeId);
        var missingAtFirst = secondEngine.GetMissingOperations(firstEngine.GetOperations().Select(operation => operation.Stamp.OperationId));
        var missingAtSecond = firstEngine.GetMissingOperations(secondEngine.GetOperations().Select(operation => operation.Stamp.OperationId));
        foreach (var operation in missingAtFirst.Reverse())
        {
            firstEngine.Receive(operation);
        }

        foreach (var operation in missingAtSecond.Reverse())
        {
            secondEngine.Receive(operation);
        }

        Assert.Equal(concurrent.StrokeId, Assert.Single(firstEngine.GetStrokes()).StrokeId);
        Assert.Equal(JsonSerializer.Serialize(firstEngine.GetStrokes()), JsonSerializer.Serialize(secondEngine.GetStrokes()));
        Assert.Equal(firstEngine.GetTombstones(), secondEngine.GetTombstones());
        Assert.Equal(firstEngine.GetOperations(), secondEngine.GetOperations());
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
        var drawingTasks = new List<Task>();
        for (int i = 0; i < 100; i++)
        {
            drawingTasks.Add(Task.Run(() => engine.BeginStroke(0, 1)));
        }

        await Task.WhenAll(drawingTasks);

        Assert.Equal(100, engine.GetStrokes().Length);
        var expectedSequences = Enumerable.Range(1, 100).Select(number => (long)number);
        var actualSequences = engine.GetOperations().Select(operation => operation.Stamp.SequenceNumber);
        Assert.Equal(expectedSequences, actualSequences);
    }
}
