using System.Text.Json;
using P2PDraw.App.Session;
using P2PDraw.Core.Models;
using P2PDraw.Core.Sync;
using Xunit;

namespace P2PDraw.Tests.App;

public class EditorSessionTests
{
    [Fact]
    public async Task FirstEditLocksSessionIdentityWithoutChangingPeerId()
    {
        await using var session = new EditorSession(Guid.NewGuid());
        var initialEngine = session.Engine;
        Guid selectedSession = Guid.NewGuid();
        Assert.False(session.IsLocked);
        Assert.False(session.EnsureSession(Guid.Empty));

        Assert.True(session.EnsureSession(selectedSession));

        Assert.True(session.IsLocked);
        Assert.Equal(selectedSession, session.Engine.SessionId);
        Assert.Equal(initialEngine.PeerId, session.Engine.PeerId);
        var lockedEngine = session.Engine;
        Assert.True(session.EnsureSession(Guid.NewGuid()));
        Assert.Same(lockedEngine, session.Engine);
    }

    [Fact]
    public async Task JoiningEmptyDocumentPreparesReplacementWithSamePeerId()
    {
        await using var session = new EditorSession(Guid.NewGuid());
        var initialEngine = session.Engine;
        Guid requestedSession = Guid.NewGuid();

        var replacement = session.PrepareJoin(requestedSession);

        Assert.NotNull(replacement);
        Assert.Same(initialEngine, session.Engine);
        Assert.Equal(requestedSession, replacement.SessionId);
        Assert.Equal(initialEngine.PeerId, replacement.PeerId);
        await session.ReplaceAsync(replacement);
        Assert.Same(replacement, session.Engine);
        Assert.False(session.IsLocked);
    }

    [Fact]
    public async Task JoiningAnotherSessionPreservesExistingDrawing()
    {
        await using var session = new EditorSession(Guid.NewGuid());
        session.EnsureSession(session.Engine.SessionId);
        var engine = session.Engine;
        var stroke = engine.BeginStroke(0, 2);
        engine.AppendStroke(stroke.StrokeId, [new(10, 20)]);
        engine.EndStroke(stroke.StrokeId);
        string before = JsonSerializer.Serialize(engine.CreateSnapshot());

        var replacement = session.PrepareJoin(Guid.NewGuid());

        Assert.Null(replacement);
        Assert.Same(engine, session.Engine);
        Assert.Equal(before, JsonSerializer.Serialize(engine.CreateSnapshot()));
        Assert.Same(engine, session.PrepareJoin(engine.SessionId));
    }

    [Fact]
    public async Task RestoringSnapshotPreparesIndependentSessionAndRetainsHistory()
    {
        var source = new SyncEngine(Guid.NewGuid(), Guid.NewGuid());
        var removed = source.BeginStroke(0, 1);
        source.RemoveStroke(removed.StrokeId);
        source.AddShape(ShapeKind.Rectangle, new(10, 20), new(30, 40), 0xFF112233, 2);
        await using var session = new EditorSession(Guid.NewGuid());
        var initialEngine = session.Engine;

        var replacement = await session.RestoreAsync(source.CreateSnapshot());

        Assert.Same(initialEngine, session.Engine);
        Assert.NotEqual(initialEngine.PeerId, replacement.PeerId);
        Assert.NotEqual(source.PeerId, replacement.PeerId);
        Assert.NotEqual(initialEngine.SessionId, replacement.SessionId);
        Assert.NotEqual(source.SessionId, replacement.SessionId);
        Assert.Equal(JsonSerializer.Serialize(source.GetShapes()), JsonSerializer.Serialize(replacement.GetShapes()));
        Assert.Equal(source.GetTombstones(), replacement.GetTombstones());
        Assert.Equal(source.GetOperations(), replacement.GetOperations());

        await session.ReplaceAsync(replacement);

        Assert.Same(replacement, session.Engine);
        Assert.True(session.IsLocked);
    }

    [Fact]
    public async Task InvalidSnapshotLeavesCurrentDocumentAndListenerIntact()
    {
        await using var session = new EditorSession(Guid.NewGuid());
        session.EnsureSession(session.Engine.SessionId);
        session.Engine.BeginStroke(0, 2);
        await session.Connection.StartListeningAsync(0);
        var engine = session.Engine;
        int port = session.Connection.Port;
        string before = JsonSerializer.Serialize(engine.CreateSnapshot());
        var source = new SyncEngine(Guid.NewGuid(), Guid.NewGuid());
        source.AddShape(ShapeKind.Ellipse, new(10, 20), new(30, 40), 0, 2);
        var invalid = source.CreateSnapshot() with { Shapes = [] };

        await Assert.ThrowsAsync<ArgumentException>(() => session.RestoreAsync(invalid));

        Assert.Same(engine, session.Engine);
        Assert.Equal(before, JsonSerializer.Serialize(engine.CreateSnapshot()));
        Assert.True(session.Connection.HasPeer);
        Assert.Equal(port, session.Connection.Port);
        Assert.False(session.IsReplacing);
    }

    [Fact]
    public async Task ReplacementStopsListenerAndKeepsEditingBlockedDuringNotification()
    {
        await using var session = new EditorSession(Guid.NewGuid());
        await session.Connection.StartListeningAsync(0);
        var replacement = new SyncEngine(Guid.NewGuid(), Guid.NewGuid());
        replacement.BeginStroke(0, 1);
        int notifications = 0;
        session.Replaced += () =>
        {
            notifications++;
            Assert.True(session.IsReplacing);
            Assert.False(session.Connection.HasPeer);
            Assert.Equal(0, session.Connection.Port);
            Assert.Same(replacement, session.Engine);
            Assert.False(session.EnsureSession(Guid.NewGuid()));
        };

        await session.ReplaceAsync(replacement);

        Assert.Equal(1, notifications);
        Assert.False(session.IsReplacing);
        Assert.True(session.IsLocked);
        await session.DisposeAsync();
        Assert.False(session.EnsureSession(replacement.SessionId));
    }
}
