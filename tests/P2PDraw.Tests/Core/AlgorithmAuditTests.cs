using P2PDraw.Core.Models;
using P2PDraw.Core.Operations;
using P2PDraw.Core.Sync;
using Xunit;

namespace P2PDraw.Tests.Core;

public class AlgorithmAuditTests
{
    [Fact]
    public void RemovedStrokeIdCannotBeReusedByAnotherBegin()
    {
        var author = new SyncEngine(Guid.NewGuid(), Guid.NewGuid());
        var begin = author.BeginStroke(0, 1);
        var remove = author.RemoveStroke(begin.StrokeId);
        var repeatedBegin = new StrokeBegin(new(Guid.NewGuid(), Guid.NewGuid(), 1, 3), begin.StrokeId, 0, 2);

        Operation[][] deliveryOrders = [[begin, remove, repeatedBegin], [repeatedBegin, begin, remove], [remove, repeatedBegin, begin]];
        foreach (var operations in deliveryOrders)
        {
            var document = new DocumentState();
            foreach (var operation in operations)
            {
                document.Apply(operation);
            }

            Assert.Empty(document.GetStrokes());
            Assert.Empty(document.GetPendingStrokeIds());
            Assert.Equal(begin.StrokeId, Assert.Single(document.GetTombstones()));
            Assert.Equal(3, document.OperationCount);
        }
    }

    [Fact]
    public void OverlappingPointBatchesUseStableOperationOrder()
    {
        var peerId = Guid.NewGuid();
        var strokeId = Guid.NewGuid();
        var earlierId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var laterId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var begin = new StrokeBegin(new(Guid.NewGuid(), peerId, 1, 1), strokeId, 0, 2);
        var firstAppend = new StrokeAppend(new(earlierId, peerId, 2, 2), strokeId, 0, [new(1, 2)]);
        var conflictingAppend = new StrokeAppend(new(laterId, peerId, 3, 2), strokeId, 0, [new(9, 9), new(3, 4)]);
        var end = new StrokeEnd(new(Guid.NewGuid(), peerId, 4, 3), strokeId, 2);
        Operation[] operations = [begin, firstAppend, conflictingAppend, end];

        for (int seed = 0; seed < 30; seed++)
        {
            var document = new DocumentState();
            var random = new Random(seed);
            var deliveryOrder = operations.OrderBy(_ => random.Next()).ToArray();
            foreach (var operation in deliveryOrder)
            {
                document.Apply(operation);
                Assert.False(document.Apply(operation));
            }

            var stroke = Assert.Single(document.GetStrokes());
            Assert.Equal(new StrokePoint[] { new(1, 2), new(3, 4) }, stroke.Points);
            Assert.True(stroke.IsComplete);
            Assert.Empty(document.GetPendingStrokeIds());
        }
    }

    [Fact]
    public void ConflictingSnapshotSequenceLeavesLocalStateUntouched()
    {
        var local = new SyncEngine(Guid.NewGuid(), Guid.NewGuid());
        var conflicting = new SyncEngine(local.PeerId, local.SessionId);
        var localBegin = local.BeginStroke(0, 1);
        conflicting.BeginStroke(0, 2);
        long localTime = local.LamportTime;

        Assert.Throws<ArgumentException>(() => local.MergeSnapshot(conflicting.CreateSnapshot()));

        Assert.Equal(localBegin.Stamp.OperationId, Assert.Single(local.GetOperations()).Stamp.OperationId);
        Assert.Equal(localBegin.StrokeId, Assert.Single(local.GetStrokes()).StrokeId);
        Assert.Empty(local.GetTombstones());
        Assert.Equal(localTime, local.LamportTime);
        Assert.Equal(2, local.EndStroke(localBegin.StrokeId).Stamp.SequenceNumber);
    }
}
