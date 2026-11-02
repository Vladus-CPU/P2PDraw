using P2PDraw.Core.Models;
using P2PDraw.Core.Sync;
using Xunit;

namespace P2PDraw.Tests.Core;

public class StrokeBatchTests
{
    [Fact]
    public void OversizedSingleAppendIsRejectedBeforeChangingState()
    {
        var engine = new SyncEngine(Guid.NewGuid(), Guid.NewGuid());
        var begin = engine.BeginStroke(0, 1);
        var points = new StrokePoint[SyncEngine.MaxPointsPerAppend + 1];

        Assert.Throws<ArgumentException>(() => engine.AppendStroke(begin.StrokeId, points));

        Assert.Single(engine.GetOperations());
        Assert.Empty(Assert.Single(engine.GetStrokes()).Points);
        Assert.Equal(1, engine.LamportTime);
        Assert.Equal(2, engine.AppendStroke(begin.StrokeId, [new(1, 2)]).Stamp.SequenceNumber);
    }

    [Fact]
    public void InvalidPointInLaterBatchDoesNotCreateEarlierOperations()
    {
        var engine = new SyncEngine(Guid.NewGuid(), Guid.NewGuid());
        var begin = engine.BeginStroke(0, 1);
        var points = new StrokePoint[SyncEngine.MaxPointsPerAppend + 1];
        points[^1] = new(double.NaN, 1);

        Assert.Throws<ArgumentException>(() => engine.AppendStrokeBatches(begin.StrokeId, points));
        Assert.Throws<ArgumentException>(() => engine.AppendStrokeBatches(begin.StrokeId, []));

        Assert.Single(engine.GetOperations());
        Assert.Empty(Assert.Single(engine.GetStrokes()).Points);
        Assert.Equal(1, engine.LamportTime);
        Assert.Equal(2, engine.AppendStroke(begin.StrokeId, [new(1, 2)]).Stamp.SequenceNumber);
    }

    [Theory]
    [InlineData(1024, 1)]
    [InlineData(1025, 2)]
    [InlineData(2048, 2)]
    [InlineData(2049, 3)]
    public void BatchesKeepPointOrderAndHaveIndependentOperationIds(int pointCount, int batchCount)
    {
        var source = new SyncEngine(Guid.NewGuid(), Guid.NewGuid());
        var begin = source.BeginStroke(0, 1);
        source.AppendStroke(begin.StrokeId, [new(-1, -2)]);
        var points = new StrokePoint[pointCount];
        for (int index = 0; index < points.Length; index++)
        {
            points[index] = new(index, -index);
        }

        var batches = source.AppendStrokeBatches(begin.StrokeId, points);
        source.EndStroke(begin.StrokeId);

        Assert.Equal(batchCount, batches.Length);
        Assert.Equal(batchCount, batches.Select(operation => operation.Stamp.OperationId).Distinct().Count());
        int expectedIndex = 1;
        foreach (var append in batches)
        {
            Assert.Equal(expectedIndex, append.StartIndex);
            Assert.InRange(append.Points.Length, 1, SyncEngine.MaxPointsPerAppend);
            expectedIndex += append.Points.Length;
        }
        var receiver = new SyncEngine(Guid.NewGuid(), source.SessionId);
        foreach (var operation in source.GetOperations().Reverse())
        {
            Assert.True(receiver.Receive(operation));
            Assert.False(receiver.Receive(operation));
        }
        var stroke = Assert.Single(receiver.GetStrokes());
        Assert.Equal(new StrokePoint(-1, -2), stroke.Points[0]);
        Assert.Equal(points, stroke.Points.Skip(1));
        Assert.True(stroke.IsComplete);
        Assert.Empty(receiver.GetPendingStrokeIds());
        Assert.Equal(batchCount + 3, source.GetOperations().Length);
        Assert.Equal(source.GetOperations(), receiver.GetOperations());
    }

    [Fact]
    public void CompletedOrMissingStrokeCannotCreateBatchOperations()
    {
        var engine = new SyncEngine(Guid.NewGuid(), Guid.NewGuid());
        var begin = engine.BeginStroke(0, 1);
        engine.EndStroke(begin.StrokeId);
        var points = new StrokePoint[SyncEngine.MaxPointsPerAppend + 1];

        Assert.Throws<InvalidOperationException>(() => engine.AppendStrokeBatches(begin.StrokeId, points));
        Assert.Throws<InvalidOperationException>(() => engine.AppendStrokeBatches(Guid.NewGuid(), points));

        Assert.Equal(2, engine.GetOperations().Length);
        Assert.Equal(2, engine.LamportTime);
    }
}
