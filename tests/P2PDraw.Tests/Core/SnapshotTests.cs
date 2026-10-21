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
        var snapshot = JsonSerializer.Deserialize<Snapshot>(JsonSerializer.Serialize(source.CreateSnapshot()))!;
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
        var a = new SyncEngine(Guid.NewGuid(), session);
        var b = new SyncEngine(Guid.NewGuid(), session);
        var first = a.BeginStroke(0, 1);
        var snapshot = a.CreateSnapshot();
        b.BeginStroke(0, 2);
        a.AppendStroke(first.StrokeId, [new(1, 2)]);
        a.EndStroke(first.StrokeId);
        b.MergeSnapshot(snapshot);
        foreach (var operation in a.GetMissingOperations(b.GetOperations().Select(op => op.Stamp.OperationId))) b.Receive(operation);
        foreach (var operation in b.GetMissingOperations(a.GetOperations().Select(op => op.Stamp.OperationId))) a.Receive(operation);
        Assert.Equal(2, b.GetStrokes().Length);
        Assert.Equal(JsonSerializer.Serialize(a.GetStrokes()), JsonSerializer.Serialize(b.GetStrokes()));
        Assert.Empty(snapshot.Strokes[0].Points);
        Assert.False(snapshot.Strokes[0].IsComplete);
    }

    [Fact]
    public void PendingAppendSurvivesSnapshotAndResolvesWhenBeginArrives()
    {
        var a = new SyncEngine(Guid.NewGuid(), Guid.NewGuid());
        var b = new SyncEngine(Guid.NewGuid(), a.SessionId);
        var source = new SyncEngine(Guid.NewGuid(), a.SessionId);
        var begin = source.BeginStroke(0, 1);
        a.Receive(source.AppendStroke(begin.StrokeId, [new(2, 3)]));
        b.MergeSnapshot(a.CreateSnapshot());
        Assert.Equal(begin.StrokeId, Assert.Single(b.GetPendingStrokeIds()));
        b.Receive(begin);
        Assert.Single(Assert.Single(b.GetStrokes()).Points);
        Assert.Empty(b.GetPendingStrokeIds());
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
