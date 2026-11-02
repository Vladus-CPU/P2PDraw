using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using P2PDraw.Core.Models;
using P2PDraw.Core.Operations;
using P2PDraw.Core.Sync;
using P2PDraw.Networking.Protocol;
using P2PDraw.Networking.Transport;
using Xunit;

namespace P2PDraw.Tests.Networking;

public class IncompleteMessageTests
{
    [Theory]
    [InlineData("shape", "Start")]
    [InlineData("shape", "End")]
    [InlineData("shape", "Start.X")]
    [InlineData("shape", "End.Y")]
    [InlineData("shape", "Kind")]
    [InlineData("shape", "Color")]
    [InlineData("shape", "Width")]
    [InlineData("shape", "Stamp")]
    [InlineData("shape", "Stamp.OperationId")]
    [InlineData("shape", "Stamp.PeerId")]
    [InlineData("shape", "Stamp.SequenceNumber")]
    [InlineData("shape", "Stamp.LamportTime")]
    [InlineData("move", "Offset")]
    [InlineData("move", "Offset.X")]
    [InlineData("move", "Offset.Y")]
    public async Task MissingOperationDataIsRejected(string kind, string missingProperty)
    {
        var engine = new SyncEngine(Guid.NewGuid(), Guid.NewGuid());
        var shape = engine.AddShape(ShapeKind.Line, new(0, 0), new(10, 20), 0, 1);
        Operation operation = shape;
        if (kind == "move")
        {
            operation = engine.MoveObject(shape.ShapeId, new(0, 0));
        }
        var message = JsonSerializer.SerializeToNode<NetworkMessage>(new OperationMessage(operation))!.AsObject();
        var data = message["Operation"]!.AsObject();
        string[] propertyPath = missingProperty.Split('.');
        if (propertyPath.Length == 1)
        {
            data.Remove(propertyPath[0]);
        }
        else
        {
            data[propertyPath[0]]!.AsObject().Remove(propertyPath[1]);
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => ReadFrameAsync(message));
    }

    [Theory]
    [InlineData("X")]
    [InlineData("Y")]
    public async Task IncompleteStrokePointIsRejected(string missingCoordinate)
    {
        var engine = new SyncEngine(Guid.NewGuid(), Guid.NewGuid());
        var begin = engine.BeginStroke(0, 1);
        var append = engine.AppendStroke(begin.StrokeId, [new(0, 0)]);
        var message = JsonSerializer.SerializeToNode<NetworkMessage>(new OperationMessage(append))!.AsObject();
        message["Operation"]!["Points"]![0]!.AsObject().Remove(missingCoordinate);

        await Assert.ThrowsAsync<InvalidDataException>(() => ReadFrameAsync(message));
    }

    [Theory]
    [InlineData("PeerId")]
    [InlineData("SessionId")]
    [InlineData("ProtocolVersion")]
    public async Task MissingHandshakeMetadataIsRejected(string missingProperty)
    {
        var hello = new HelloMessage(Guid.NewGuid(), Guid.NewGuid(), TcpPeer.ProtocolVersion);
        var message = JsonSerializer.SerializeToNode<NetworkMessage>(hello)!.AsObject();
        message.Remove(missingProperty);

        await Assert.ThrowsAsync<InvalidDataException>(() => ReadFrameAsync(message));
    }

    [Fact]
    public async Task OptionalListeningPortAndFillCanBeOmitted()
    {
        var hello = new HelloMessage(Guid.NewGuid(), Guid.NewGuid(), TcpPeer.ProtocolVersion);
        var helloJson = JsonSerializer.SerializeToNode<NetworkMessage>(hello)!.AsObject();
        helloJson.Remove("ListeningPort");
        var engine = new SyncEngine(Guid.NewGuid(), Guid.NewGuid());
        var shape = engine.AddShape(ShapeKind.Rectangle, new(0, 0), new(10, 20), 0, 1);
        var shapeJson = JsonSerializer.SerializeToNode<NetworkMessage>(new OperationMessage(shape))!.AsObject();
        shapeJson["Operation"]!.AsObject().Remove("Fill");

        var receivedHello = Assert.IsType<HelloMessage>(await ReadFrameAsync(helloJson));
        var received = Assert.IsType<OperationMessage>(await ReadFrameAsync(shapeJson));

        Assert.Equal(0, receivedHello.ListeningPort);
        var receivedShape = Assert.IsType<ShapeAdd>(received.Operation);
        Assert.Null(receivedShape.Fill);
        Assert.Equal(new StrokePoint(0, 0), receivedShape.Start);
        Assert.Equal(shape.Stamp, receivedShape.Stamp);
    }

    private static async Task<NetworkMessage?> ReadFrameAsync(JsonObject message)
    {
        byte[] payload = Encoding.UTF8.GetBytes(message.ToJsonString());
        byte[] frame = new byte[payload.Length + 4];
        BinaryPrimitives.WriteInt32BigEndian(frame, payload.Length);
        payload.CopyTo(frame, 4);
        using var stream = new MemoryStream(frame);
        return await MessageFraming.ReadAsync<NetworkMessage>(stream, TestContext.Current.CancellationToken);
    }
}
