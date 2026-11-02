using System.Collections.Immutable;
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
    [Theory]
    [InlineData(1025, true)]
    [InlineData(80000, false)]
    public async Task ImportedOperationsMustFitTransportWithoutRewritingTheirIds(int pointCount, bool canOpen)
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(EditorTests));
        await session.Dispatch(async () =>
        {
            var source = new SyncEngine(Guid.NewGuid(), Guid.NewGuid());
            var begin = source.BeginStroke(0, 1);
            var points = Enumerable.Repeat(new StrokePoint(-double.MaxValue, double.MaxValue), pointCount).ToImmutableArray();
            var append = new StrokeAppend(new(Guid.NewGuid(), source.PeerId, 2, 2), begin.StrokeId, 0, points);
            source.Receive(append);
            source.EndStroke(begin.StrokeId);
            Assert.Equal(canOpen, MessageFraming.FitsInFrame<NetworkMessage>(new OperationMessage(append)));
            await using var model = new MainWindowViewModel();
            model.BeginStroke(new(1, 2));
            model.EndStroke();
            string previousSession = model.SessionId;
            string previousDocument = Normalize(model);

            if (canOpen)
            {
                Assert.True(await model.OpenAsNewSessionAsync(source.CreateSnapshot()));
                Assert.Contains(model.GetSnapshot().Operations, operation => operation.Stamp.OperationId == append.Stamp.OperationId);
                Assert.Equal(points, Assert.Single(model.Strokes).Points);
            }
            else
            {
                await Assert.ThrowsAsync<ArgumentException>(() => model.OpenAsNewSessionAsync(source.CreateSnapshot()));
                Assert.Equal(previousSession, model.SessionId);
                Assert.Equal(previousDocument, Normalize(model));
                Assert.True(model.CanUndo);
            }
            return true;
        }, TestContext.Current.CancellationToken);
    }
}
