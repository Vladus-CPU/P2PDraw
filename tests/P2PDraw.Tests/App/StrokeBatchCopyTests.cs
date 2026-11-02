using Avalonia.Headless;
using P2PDraw.App.ViewModels;
using P2PDraw.Core.Models;
using P2PDraw.Core.Operations;
using P2PDraw.Core.Sync;
using P2PDraw.Networking.Protocol;
using Xunit;

namespace P2PDraw.Tests.App;

public partial class EditorTests
{
    [Fact]
    public async Task DuplicateAndRedoLargeStrokeCreateOnlyFramableOperations()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(EditorTests));
        await session.Dispatch(async () =>
        {
            var source = new SyncEngine(Guid.NewGuid(), Guid.NewGuid());
            var begin = source.BeginStroke(0xFF112233, 3);
            var points = new StrokePoint[SyncEngine.MaxPointsPerAppend * 2 + 1];
            for (int index = 0; index < points.Length; index++)
            {
                points[index] = new(index, -index);
            }
            source.AppendStrokeBatches(begin.StrokeId, points);
            source.EndStroke(begin.StrokeId);
            await using var model = new MainWindowViewModel();
            Assert.True(await model.OpenAsNewSessionAsync(source.CreateSnapshot()));

            model.DuplicateObject(begin.StrokeId);

            var copy = model.Strokes.Single(stroke => stroke.StrokeId != begin.StrokeId);
            Assert.Equal(points, copy.Points);
            Assert.True(copy.IsComplete);
            Assert.Equal(new StrokePoint(16, 16), copy.Offset);
            model.Undo();
            Assert.Single(model.Strokes);
            Assert.Contains(copy.StrokeId, model.GetSnapshot().Tombstones);
            model.Redo();
            var recreated = model.Strokes.Single(stroke => stroke.StrokeId != begin.StrokeId);
            Assert.NotEqual(copy.StrokeId, recreated.StrokeId);
            Assert.Equal(points, recreated.Points);
            Assert.True(recreated.IsComplete);
            Assert.Equal(copy.Offset, recreated.Offset);
            foreach (var operation in model.GetSnapshot().Operations)
            {
                Assert.True(MessageFraming.FitsInFrame<NetworkMessage>(new OperationMessage(operation)));
                if (operation is StrokeAppend append)
                {
                    Assert.InRange(append.Points.Length, 1, SyncEngine.MaxPointsPerAppend);
                }
            }
            return true;
        }, TestContext.Current.CancellationToken);
    }
}
