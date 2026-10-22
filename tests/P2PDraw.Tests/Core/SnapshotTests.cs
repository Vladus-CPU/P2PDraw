using System.Text.Json;
using P2PDraw.Core.Sync;
using Xunit;

namespace P2PDraw.Tests.Core;

public class SnapshotTests
{
    [Fact]
    public void JsonSnapshotRestoresStrokesTombstonesAndDuplicateTracking()
    {
        var sourceEngine = new SyncEngine(Guid.NewGuid(), Guid.NewGuid());
        var removed = sourceEngine.BeginStroke(0, 1);
        sourceEngine.RemoveStroke(removed.StrokeId);
        var visible = sourceEngine.BeginStroke(0xFF112233, 3);
        sourceEngine.AppendStroke(visible.StrokeId, [new(1, 2), new(3, 4)]);
        sourceEngine.EndStroke(visible.StrokeId);
        string snapshotJson = JsonSerializer.Serialize(sourceEngine.CreateSnapshot());
        var snapshot = JsonSerializer.Deserialize<Snapshot>(snapshotJson)!;
        var restoredEngine = new SyncEngine(Guid.NewGuid(), sourceEngine.SessionId);
        restoredEngine.MergeSnapshot(snapshot);
        Assert.Equal(JsonSerializer.Serialize(sourceEngine.GetStrokes()), JsonSerializer.Serialize(restoredEngine.GetStrokes()));
        Assert.Equal(sourceEngine.GetTombstones(), restoredEngine.GetTombstones());
        long restoredTime = restoredEngine.LamportTime;
        foreach (var operation in snapshot.Operations)
        {
            Assert.False(restoredEngine.Receive(operation));
        }

        restoredEngine.MergeSnapshot(snapshot);
        Assert.Equal(restoredTime, restoredEngine.LamportTime);
    }

    [Fact]
    public void LocalEditsAndChangesAfterSnapshotArePreserved()
    {
        var session = Guid.NewGuid();
        var firstEngine = new SyncEngine(Guid.NewGuid(), session);
        var secondEngine = new SyncEngine(Guid.NewGuid(), session);
        var first = firstEngine.BeginStroke(0, 1);
        var snapshot = firstEngine.CreateSnapshot();
        secondEngine.BeginStroke(0, 2);
        firstEngine.AppendStroke(first.StrokeId, [new(1, 2)]);
        firstEngine.EndStroke(first.StrokeId);
        secondEngine.MergeSnapshot(snapshot);
        var secondKnownIds = secondEngine.GetOperations().Select(operation => operation.Stamp.OperationId);
        var missingAtSecond = firstEngine.GetMissingOperations(secondKnownIds);
        foreach (var operation in missingAtSecond)
        {
            secondEngine.Receive(operation);
        }

        var firstKnownIds = firstEngine.GetOperations().Select(operation => operation.Stamp.OperationId);
        var missingAtFirst = secondEngine.GetMissingOperations(firstKnownIds);
        foreach (var operation in missingAtFirst)
        {
            firstEngine.Receive(operation);
        }

        Assert.Equal(2, secondEngine.GetStrokes().Length);
        Assert.Equal(JsonSerializer.Serialize(firstEngine.GetStrokes()), JsonSerializer.Serialize(secondEngine.GetStrokes()));
        Assert.Empty(snapshot.Strokes[0].Points);
        Assert.False(snapshot.Strokes[0].IsComplete);
    }

    [Fact]
    public void PendingAppendSurvivesSnapshotAndResolvesWhenBeginArrives()
    {
        var firstEngine = new SyncEngine(Guid.NewGuid(), Guid.NewGuid());
        var secondEngine = new SyncEngine(Guid.NewGuid(), firstEngine.SessionId);
        var sourceEngine = new SyncEngine(Guid.NewGuid(), firstEngine.SessionId);
        var begin = sourceEngine.BeginStroke(0, 1);
        firstEngine.Receive(sourceEngine.AppendStroke(begin.StrokeId, [new(2, 3)]));
        secondEngine.MergeSnapshot(firstEngine.CreateSnapshot());
        Assert.Equal(begin.StrokeId, Assert.Single(secondEngine.GetPendingStrokeIds()));
        secondEngine.Receive(begin);
        var restoredStroke = Assert.Single(secondEngine.GetStrokes());
        Assert.Single(restoredStroke.Points);
        Assert.Empty(secondEngine.GetPendingStrokeIds());
    }

    [Fact]
    public void InvalidSnapshotDoesNotPartiallyChangeStateOrClock()
    {
        var engine = new SyncEngine(Guid.NewGuid(), Guid.NewGuid());
        engine.BeginStroke(0, 1);
        var snapshot = engine.CreateSnapshot();
        var otherEngine = new SyncEngine(Guid.NewGuid(), engine.SessionId);
        otherEngine.BeginStroke(0, 1);
        var invalidSnapshot = otherEngine.CreateSnapshot() with { Tombstones = [Guid.NewGuid()] };
        Assert.Throws<ArgumentException>(() => engine.MergeSnapshot(invalidSnapshot));
        Assert.Throws<ArgumentException>(() => engine.MergeSnapshot(snapshot with { SessionId = Guid.NewGuid() }));
        Assert.Throws<ArgumentException>(() => engine.MergeSnapshot(snapshot with { Operations = default }));
        Assert.Throws<ArgumentException>(() => engine.MergeSnapshot(snapshot with { LamportTime = 0 }));
        Assert.Throws<ArgumentException>(() => engine.MergeSnapshot(snapshot with { PendingStrokeIds = default }));
        Assert.Throws<ArgumentException>(() => engine.MergeSnapshot(snapshot with { Strokes = [snapshot.Strokes[0] with { Points = default }] }));
        Assert.Equal(JsonSerializer.Serialize(snapshot), JsonSerializer.Serialize(engine.CreateSnapshot()));
    }

    [Fact]
    public void RestoredAuthorContinuesSequenceAfterSnapshot()
    {
        var sourceEngine = new SyncEngine(Guid.NewGuid(), Guid.NewGuid());
        sourceEngine.BeginStroke(0, 1);
        var restoredEngine = new SyncEngine(sourceEngine.PeerId, sourceEngine.SessionId);
        restoredEngine.MergeSnapshot(sourceEngine.CreateSnapshot());
        Assert.Equal(2, restoredEngine.BeginStroke(0, 1).Stamp.SequenceNumber);
    }
}
