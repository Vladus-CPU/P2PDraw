using P2PDraw.Core.Sync;
using System.Text.Json;
using Xunit;

namespace P2PDraw.Tests.Core;

public class SnapshotTests
{
    [Fact]
    public void JsonSnapshotRestoresStrokesTombstonesAndDuplicateTracking()
    {
        var source = new SyncEngine(Guid.NewGuid(), Guid.NewGuid());
        var removed = source.BeginStroke(0, 1);
        source.RemoveStroke(removed.StrokeId);
        var visible = source.BeginStroke(0xFF112233, 3);
        source.AppendStroke(visible.StrokeId, [new(1, 2), new(3, 4)]);
        source.EndStroke(visible.StrokeId);
        string snapshotJson = JsonSerializer.Serialize(source.CreateSnapshot());
        var snapshot = JsonSerializer.Deserialize<Snapshot>(snapshotJson)!;
        var restored = new SyncEngine(Guid.NewGuid(), source.SessionId);
        restored.MergeSnapshot(snapshot);
        Assert.Equal(JsonSerializer.Serialize(source.GetStrokes()), JsonSerializer.Serialize(restored.GetStrokes()));
        Assert.Equal(source.GetTombstones(), restored.GetTombstones());
        long time = restored.LamportTime;
        foreach (var operation in snapshot.Operations) Assert.False(restored.Receive(operation));
        restored.MergeSnapshot(snapshot);
        Assert.Equal(time, restored.LamportTime);
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
        foreach (var operation in missingAtSecond) secondEngine.Receive(operation);

        var firstKnownIds = firstEngine.GetOperations().Select(operation => operation.Stamp.OperationId);
        var missingAtFirst = secondEngine.GetMissingOperations(firstKnownIds);
        foreach (var operation in missingAtFirst) firstEngine.Receive(operation);
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
        var source = new SyncEngine(Guid.NewGuid(), firstEngine.SessionId);
        var begin = source.BeginStroke(0, 1);
        firstEngine.Receive(source.AppendStroke(begin.StrokeId, [new(2, 3)]));
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
        var other = new SyncEngine(Guid.NewGuid(), engine.SessionId);
        other.BeginStroke(0, 1);
        var invalid = other.CreateSnapshot() with { Tombstones = [Guid.NewGuid()] };
        Assert.Throws<ArgumentException>(() => engine.MergeSnapshot(invalid));
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
        var source = new SyncEngine(Guid.NewGuid(), Guid.NewGuid());
        source.BeginStroke(0, 1);
        var restored = new SyncEngine(source.PeerId, source.SessionId);
        restored.MergeSnapshot(source.CreateSnapshot());
        Assert.Equal(2, restored.BeginStroke(0, 1).Stamp.SequenceNumber);
    }
}
