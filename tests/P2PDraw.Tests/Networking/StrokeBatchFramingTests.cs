using P2PDraw.Core.Models;
using P2PDraw.Core.Sync;
using P2PDraw.Networking.Protocol;
using Xunit;

namespace P2PDraw.Tests.Networking;

public class StrokeBatchFramingTests
{
    [Fact]
    public async Task EveryBatchFitsInFrameWithExtremeFiniteCoordinates()
    {
        var source = new SyncEngine(Guid.NewGuid(), Guid.NewGuid());
        var begin = source.BeginStroke(0, 1);
        var points = new StrokePoint[SyncEngine.MaxPointsPerAppend * 2 + 1];
        for (int index = 0; index < points.Length; index++)
        {
            points[index] = new(-double.MaxValue, double.MaxValue);
        }
        var batches = source.AppendStrokeBatches(begin.StrokeId, points);
        source.EndStroke(begin.StrokeId);
        var receiver = new SyncEngine(Guid.NewGuid(), source.SessionId);
        var cancellationToken = TestContext.Current.CancellationToken;

        foreach (var operation in source.GetOperations())
        {
            var envelope = new OperationMessage(operation);
            Assert.True(MessageFraming.FitsInFrame<NetworkMessage>(envelope));
            using var stream = new MemoryStream();
            await MessageFraming.WriteAsync<NetworkMessage>(stream, envelope, cancellationToken);
            Assert.InRange(stream.Length, 5, MessageFraming.MaxMessageSize + 4L);
            stream.Position = 0;
            var restored = Assert.IsType<OperationMessage>(await MessageFraming.ReadAsync<NetworkMessage>(stream, cancellationToken));
            Assert.Equal(operation.Stamp.OperationId, restored.Operation.Stamp.OperationId);
            receiver.Receive(restored.Operation);
        }

        Assert.Equal(3, batches.Length);
        var stroke = Assert.Single(receiver.GetStrokes());
        Assert.Equal(points, stroke.Points);
        Assert.True(stroke.IsComplete);
        Assert.Empty(receiver.GetPendingStrokeIds());
    }
}
