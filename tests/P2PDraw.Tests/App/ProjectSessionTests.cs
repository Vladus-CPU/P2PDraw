using System.Text.Json;
using Avalonia.Headless;
using P2PDraw.App.ViewModels;
using P2PDraw.Core.Models;
using P2PDraw.Core.Sync;
using Xunit;

namespace P2PDraw.Tests.App;

public partial class EditorTests
{
    [Fact]
    public async Task OpenProjectForksSessionAndLeavesOtherPeerDocumentIntact()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(EditorTests));
        await session.Dispatch(async () =>
        {
            string oldSession = Guid.NewGuid().ToString();
            await using var first = new MainWindowViewModel(["0", oldSession, "1"]);
            await using var second = new MainWindowViewModel(["0", oldSession, "1"]);
            await first.StartListeningAsync();
            await second.StartListeningAsync();
            first.RemotePort = second.LocalPort;
            await first.ConnectAsync();
            first.AddShape(ShapeKind.Rectangle, new(10, 20), new(30, 40));
            await WaitUntilAsync(() => second.OperationCount == 1);
            string otherDocument = Normalize(second);
            Assert.True(first.CanUndo);
            first.LayerEditor.ToggleVisibility(Layer.DefaultLayerId);

            var saved = new SyncEngine(Guid.NewGuid(), Guid.NewGuid());
            var layer = saved.AddLayer("Імпортований шар");
            var shape = saved.AddShape(ShapeKind.Ellipse, new(1, 2), new(3, 4), 0xFF112233, 2, layerId: layer.LayerId);
            saved.ResizeShape(shape.ShapeId, new(5, 6), new(30, 40));
            saved.MoveObject(shape.ShapeId, new(100, 200));
            saved.BeginStroke(0, 1);
            saved.ClearLayer(Layer.DefaultLayerId);

            Assert.True(await first.OpenAsNewSessionAsync(saved.CreateSnapshot()));

            Assert.NotEqual(oldSession, first.SessionId);
            Assert.NotEqual(saved.SessionId.ToString(), first.SessionId);
            Assert.Equal(0, first.ConnectedPeers);
            await WaitUntilAsync(() => second.ConnectedPeers == 0);
            Assert.Equal(otherDocument, Normalize(second));
            Assert.Equal(JsonSerializer.Serialize(saved.GetShapes()), JsonSerializer.Serialize(first.Shapes));
            Assert.Equal(saved.GetTombstones(), first.GetSnapshot().Tombstones);
            Assert.Contains(Layer.DefaultLayerId, first.LayerEditor.VisibleLayerIds);
            Assert.False(first.CanUndo);
            Assert.False(first.CanRedo);

            await first.CreateSessionAsync();
            await using var joining = new MainWindowViewModel(["0", first.SessionId, "1"]);
            await joining.StartListeningAsync();
            joining.RemotePort = first.LocalPort;
            await joining.ConnectAsync();
            await WaitUntilAsync(() => joining.OperationCount == first.OperationCount);
            Assert.Equal(Normalize(first), Normalize(joining));
            Assert.Equal(otherDocument, Normalize(second));
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task InvalidProjectStateDoesNotDisconnectOrReplaceCurrentSession()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(EditorTests));
        await session.Dispatch(async () =>
        {
            string sessionId = Guid.NewGuid().ToString();
            await using var first = new MainWindowViewModel(["0", sessionId, "1"]);
            await using var second = new MainWindowViewModel(["0", sessionId, "1"]);
            await first.StartListeningAsync();
            await second.StartListeningAsync();
            first.RemotePort = second.LocalPort;
            await first.ConnectAsync();
            first.AddShape(ShapeKind.Line, new(1, 2), new(3, 4));
            await WaitUntilAsync(() => second.OperationCount == 1);
            string before = Normalize(first);
            var invalid = first.GetSnapshot() with { Shapes = [] };

            await Assert.ThrowsAsync<ArgumentException>(() => first.OpenAsNewSessionAsync(invalid));

            Assert.Equal(sessionId, first.SessionId);
            Assert.Equal(1, first.ConnectedPeers);
            Assert.Equal(before, Normalize(first));
            Assert.Equal(before, Normalize(second));
            Assert.True(first.CanUndo);
            return true;
        }, TestContext.Current.CancellationToken);
    }
}
