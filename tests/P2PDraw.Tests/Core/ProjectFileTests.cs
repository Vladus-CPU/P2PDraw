using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using P2PDraw.Core.Models;
using P2PDraw.Core.Storage;
using P2PDraw.Core.Sync;
using Xunit;

namespace P2PDraw.Tests.Core;

public class ProjectFileTests
{
    [Fact]
    public void RoundTripPreservesDocumentLayersLogAndTombstones()
    {
        var engine = new SyncEngine(Guid.NewGuid(), Guid.NewGuid());
        var layer = engine.AddLayer("Ескіз");
        var stroke = engine.BeginStroke(0xFF123456, 3, layer.LayerId);
        engine.AppendStroke(stroke.StrokeId, [new(-10, 20), new(30, 40)]);
        engine.EndStroke(stroke.StrokeId);
        engine.MoveObject(stroke.StrokeId, new(4, 5));
        var shape = engine.AddShape(ShapeKind.Rectangle, new(10, 20), new(30, 40), 0xFFFF0000, 2, 0x80FF0000);
        engine.ResizeShape(shape.ShapeId, new(5, 10), new(50, 60));
        var removed = engine.AddLayer("Видалений");
        engine.AddShape(ShapeKind.Line, new(0, 0), new(10, 10), 0xFF000000, 1, layerId: removed.LayerId);
        engine.RemoveLayer(removed.LayerId);
        var deleted = engine.AddShape(ShapeKind.Ellipse, new(0, 0), new(1, 1), 0xFF000000, 1);
        engine.RemoveObject(deleted.ShapeId);
        var original = ProjectFile.Create("Курсовий малюнок", engine.CreateSnapshot());

        var restored = ProjectFile.Deserialize(original.Serialize());

        Assert.Equal(ProjectFile.CurrentVersion, restored.FormatVersion);
        Assert.Equal(original.Name, restored.Name);
        Assert.Equal(original.SavedAt, restored.SavedAt);
        Assert.Equal(TimeSpan.Zero, restored.SavedAt.Offset);
        Assert.Equal(JsonSerializer.Serialize(original.Snapshot), JsonSerializer.Serialize(restored.Snapshot));
        var peer = new SyncEngine(Guid.NewGuid(), restored.Snapshot.SessionId);
        peer.MergeSnapshot(restored.Snapshot);
        Assert.Equal(JsonSerializer.Serialize(engine.GetStrokes()), JsonSerializer.Serialize(peer.GetStrokes()));
        Assert.Equal(JsonSerializer.Serialize(engine.GetShapes()), JsonSerializer.Serialize(peer.GetShapes()));
        Assert.Equal(engine.GetOperations().Length, peer.GetOperations().Length);
        Assert.Single(peer.CreateSnapshot().LayerTombstones);
        Assert.Single(peer.GetTombstones());
    }

    [Fact]
    public void RoundTripPreservesOperationsWaitingForMissingStrokeAndLayer()
    {
        Guid sessionId = Guid.NewGuid();
        var source = new SyncEngine(Guid.NewGuid(), sessionId);
        var layer = source.AddLayer("Ще не отриманий");
        var stroke = source.BeginStroke(0xFF000000, 2, layer.LayerId);
        var append = source.AppendStroke(stroke.StrokeId, [new(1, 2)]);
        var receiver = new SyncEngine(Guid.NewGuid(), sessionId);
        receiver.Receive(append);
        receiver.Receive(stroke);
        var file = ProjectFile.Create("Неповний журнал", receiver.CreateSnapshot());

        var restoredFile = ProjectFile.Deserialize(file.Serialize());
        var restored = new SyncEngine(Guid.NewGuid(), sessionId);
        restored.MergeSnapshot(restoredFile.Snapshot);

        Assert.Equal(JsonSerializer.Serialize(file.Snapshot), JsonSerializer.Serialize(restoredFile.Snapshot));
        Assert.Single(restored.GetPendingLayerIds());
        restored.Receive(layer);
        Assert.Single(restored.GetStrokes());
        Assert.Equal(new StrokePoint(1, 2), Assert.Single(restored.GetStrokes()).Points[0]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void UnsupportedFormatVersionIsRejected(int version)
    {
        var document = ValidJson();
        document["FormatVersion"] = version;

        var error = Assert.Throws<InvalidDataException>(() => ProjectFile.Deserialize(document.ToJsonString()));

        Assert.Contains("не підтримується", error.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void MissingProjectNameIsRejected(string? name)
    {
        var document = ValidJson();
        document["Name"] = name;

        Assert.Throws<InvalidDataException>(() => ProjectFile.Deserialize(document.ToJsonString()));
    }

    [Fact]
    public void LongProjectNameIsRejected()
    {
        var document = ValidJson();
        document["Name"] = new string('A', 201);

        Assert.Throws<InvalidDataException>(() => ProjectFile.Deserialize(document.ToJsonString()));
    }

    [Theory]
    [InlineData("0001-01-01T00:00:00+00:00")]
    [InlineData("2026-11-02T12:00:00+02:00")]
    [InlineData("невідома дата")]
    public void InvalidOrNonUtcDateIsRejected(string date)
    {
        var document = ValidJson();
        document["SavedAt"] = date;

        Assert.Throws<InvalidDataException>(() => ProjectFile.Deserialize(document.ToJsonString()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"FormatVersion\": 1}")]
    [InlineData("{invalid}")]
    public void InvalidOrIncompleteJsonIsRejected(string json)
    {
        Assert.Throws<InvalidDataException>(() => ProjectFile.Deserialize(json));
    }

    [Fact]
    public void UnknownPropertiesAreRejected()
    {
        var document = ValidJson();
        document["UnknownFeature"] = true;

        Assert.Throws<InvalidDataException>(() => ProjectFile.Deserialize(document.ToJsonString()));
    }

    [Fact]
    public void SnapshotWithoutMatchingLogIsRejected()
    {
        var document = ValidJson();
        document["Snapshot"]!["Operations"] = new JsonArray();

        Assert.Throws<InvalidDataException>(() => ProjectFile.Deserialize(document.ToJsonString()));
    }

    [Fact]
    public void SnapshotWithDuplicateOperationIsRejected()
    {
        var document = ValidJson();
        var operations = document["Snapshot"]!["Operations"]!.AsArray();
        operations.Add(operations[0]!.DeepClone());

        Assert.Throws<InvalidDataException>(() => ProjectFile.Deserialize(document.ToJsonString()));
    }

    [Fact]
    public void Utf8BomIsAcceptedAndInvalidUtf8IsRejected()
    {
        string json = ValidJson().ToJsonString();
        byte[] bytes = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(json)];

        var file = ProjectFile.Deserialize(bytes);

        Assert.Equal("Малюнок", file.Name);
        Assert.Throws<InvalidDataException>(() => ProjectFile.Deserialize(new byte[] { 0xFF, 0xFE, 0xFD }));
    }

    [Fact]
    public void OversizedFileIsRejectedBeforeParsing()
    {
        var contents = new byte[ProjectFile.MaximumFileBytes + 1];

        var error = Assert.Throws<InvalidDataException>(() => ProjectFile.Deserialize(contents));

        Assert.Contains("32 МіБ", error.Message);
    }

    private static JsonObject ValidJson()
    {
        var engine = new SyncEngine(Guid.NewGuid(), Guid.NewGuid());
        engine.AddShape(ShapeKind.Rectangle, new(0, 0), new(10, 10), 0xFF000000, 2);
        var file = ProjectFile.Create("Малюнок", engine.CreateSnapshot());
        return JsonNode.Parse(file.Serialize())!.AsObject();
    }
}
