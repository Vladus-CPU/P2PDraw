using P2PDraw.Core.Models;
using P2PDraw.Core.Operations;
using P2PDraw.Core.Sync;

namespace P2PDraw.App.Editing;

public sealed class EditHistory
{
    private readonly Func<SyncEngine> getEngine;
    private readonly Action<Operation> publish;
    private readonly ObjectDuplicator duplicator;
    private readonly Stack<Edit> undo = [];
    private readonly Stack<Edit> redo = [];
    private readonly Dictionary<Guid, Guid> replacements = [];
    private readonly Dictionary<Guid, Revision> revisions = [];

    public event Action? Changed;
    public bool CanUndo => undo.Count > 0;
    public bool CanRedo => redo.Count > 0;

    public EditHistory(Func<SyncEngine> getEngine, Action<Operation> publish)
    {
        this.getEngine = getEngine;
        this.publish = publish;
        duplicator = new ObjectDuplicator(getEngine, publish);
    }

    public void RecordCreation(Guid objectId)
    {
        var engine = getEngine();
        var shape = engine.GetShapes().FirstOrDefault(item => item.ShapeId == objectId);
        var stroke = engine.GetStrokes().FirstOrDefault(item => item.StrokeId == objectId);
        redo.Clear();
        if (shape?.PeerId == engine.PeerId || stroke?.PeerId == engine.PeerId)
        {
            revisions[objectId] = ReadRevision(objectId);
            var stamp = shape?.CreatedAt ?? stroke!.CreatedAt;
            undo.Push(new Edit(EditKind.Creation, objectId, stamp, BeforeShape: shape, Stroke: stroke));
        }
        Changed?.Invoke();
    }

    public void RecordMove(ObjectMove operation)
    {
        var engine = getEngine();
        if (operation.Stamp.PeerId != engine.PeerId)
        {
            return;
        }
        // The UI copy can lag behind an operation received just before this local action.
        ObjectMove? previous = null;
        foreach (var item in engine.GetOperations())
        {
            if (item is ObjectMove move && move.ObjectId == operation.ObjectId
                && move.Stamp.CompareTo(operation.Stamp) < 0 && (previous is null || move.Stamp.CompareTo(previous.Stamp) > 0))
            {
                previous = move;
            }
        }
        var before = previous?.Offset ?? default;
        var revision = GetRevision(operation.ObjectId);
        revisions[operation.ObjectId] = revision with { MoveStamp = operation.Stamp };
        undo.Push(new Edit(EditKind.Move, operation.ObjectId, operation.Stamp, before, operation.Offset));
        redo.Clear();
        Changed?.Invoke();
    }

    public void RecordResize(ShapeResize operation)
    {
        var engine = getEngine();
        if (operation.Stamp.PeerId != engine.PeerId)
        {
            return;
        }
        ShapeAdd? creation = null;
        ShapeResize? previous = null;
        foreach (var item in engine.GetOperations())
        {
            if (item is ShapeAdd added && added.ShapeId == operation.ShapeId
                && (creation is null || added.Stamp.CompareTo(creation.Stamp) < 0))
            {
                creation = added;
            }
            if (item is ShapeResize resized && resized.ShapeId == operation.ShapeId
                && resized.Stamp.CompareTo(operation.Stamp) < 0 && (previous is null || resized.Stamp.CompareTo(previous.Stamp) > 0))
            {
                previous = resized;
            }
        }
        if (creation is null)
        {
            return;
        }
        if (previous is not null && previous.Stamp.CompareTo(creation.Stamp) <= 0)
        {
            previous = null;
        }
        var before = new Shape(operation.ShapeId, creation.Stamp.PeerId, creation.Kind, previous?.Start ?? creation.Start,
            previous?.End ?? creation.End, creation.Color, creation.Width, creation.Stamp, creation.Fill)
        {
            ResizedAt = previous?.Stamp, LayerId = creation.LayerId
        };
        var after = before with { Start = operation.Start, End = operation.End, ResizedAt = operation.Stamp };
        var revision = GetRevision(operation.ShapeId);
        revisions[operation.ShapeId] = revision with { ResizeStamp = operation.Stamp };
        undo.Push(new Edit(EditKind.Resize, operation.ShapeId, operation.Stamp, BeforeShape: before, AfterShape: after));
        redo.Clear();
        Changed?.Invoke();
    }

    public bool Undo()
    {
        if (!undo.TryPop(out var edit))
        {
            return false;
        }
        var applied = Apply(edit, undoing: true);
        if (applied is not null)
        {
            redo.Push(applied);
        }
        Changed?.Invoke();
        return applied is not null;
    }

    public bool Redo()
    {
        if (!redo.TryPop(out var edit))
        {
            return false;
        }
        var applied = Apply(edit, undoing: false);
        if (applied is not null)
        {
            undo.Push(applied);
        }
        Changed?.Invoke();
        return applied is not null;
    }

    public void DiscardRedo()
    {
        redo.Clear();
        Changed?.Invoke();
    }

    public void Clear()
    {
        undo.Clear();
        redo.Clear();
        replacements.Clear();
        revisions.Clear();
        Changed?.Invoke();
    }

    private Edit? Apply(Edit edit, bool undoing)
    {
        var engine = getEngine();
        Guid objectId = ResolveId(edit.ObjectId);
        if (edit.Kind == EditKind.Creation)
        {
            return undoing ? UndoCreation(edit, objectId) : RedoCreation(edit, objectId);
        }
        foreach (var operation in engine.GetOperations())
        {
            if (operation.Stamp.PeerId == engine.PeerId || operation.Stamp.CompareTo(edit.SourceStamp) <= 0)
            {
                continue;
            }
            // Keep an intervening foreign edit when older local actions are undone.
            bool changedProperty = edit.Kind == EditKind.Move && operation is ObjectMove move && move.ObjectId == objectId
                || edit.Kind == EditKind.Resize && operation is ShapeResize foreignResize && foreignResize.ShapeId == objectId;
            if (changedProperty)
            {
                return null;
            }
        }
        var revision = GetRevision(objectId);
        if (edit.Kind == EditKind.Move)
        {
            var expected = undoing ? edit.AfterOffset : edit.BeforeOffset;
            var target = undoing ? edit.BeforeOffset : edit.AfterOffset;
            var operation = engine.TryMoveObject(objectId, expected, revision.MoveStamp, target);
            if (operation is null)
            {
                return null;
            }
            revisions[objectId] = revision with { MoveStamp = operation.Stamp };
            publish(operation);
            return edit;
        }
        var expectedShape = (undoing ? edit.AfterShape : edit.BeforeShape)!;
        var targetShape = (undoing ? edit.BeforeShape : edit.AfterShape)!;
        expectedShape = expectedShape with { ShapeId = objectId, ResizedAt = revision.ResizeStamp };
        var resize = engine.TryResizeShape(expectedShape, targetShape.Start, targetShape.End);
        if (resize is null)
        {
            return null;
        }
        revisions[objectId] = revision with { ResizeStamp = resize.Stamp };
        publish(resize);
        return edit;
    }

    private Edit? UndoCreation(Edit edit, Guid objectId)
    {
        var engine = getEngine();
        foreach (var operation in engine.GetOperations())
        {
            if (operation.Stamp.PeerId == engine.PeerId)
            {
                continue;
            }
            bool changedByAnotherPeer = operation switch
            {
                ObjectMove move => move.ObjectId == objectId,
                ShapeResize resize => resize.ShapeId == objectId,
                ObjectLayer layer => layer.ObjectId == objectId,
                _ => false
            };
            if (changedByAnotherPeer)
            {
                return null;
            }
        }
        var shape = engine.GetShapes().FirstOrDefault(item => item.ShapeId == objectId);
        var stroke = engine.GetStrokes().FirstOrDefault(item => item.StrokeId == objectId);
        var revision = GetRevision(objectId);
        var operationToPublish = engine.TryRemoveObject(objectId, revision.MoveStamp, revision.ResizeStamp, revision.LayerStamp);
        if (operationToPublish is null)
        {
            return null;
        }
        publish(operationToPublish);
        return edit with { BeforeShape = shape, Stroke = stroke };
    }

    private Edit? RedoCreation(Edit edit, Guid objectId)
    {
        try
        {
            Guid replacement;
            if (edit.BeforeShape is not null)
            {
                replacement = duplicator.Copy(edit.BeforeShape, default);
            }
            else if (edit.Stroke is not null)
            {
                replacement = duplicator.Copy(edit.Stroke, default);
            }
            else
            {
                return null;
            }
            replacements[objectId] = replacement;
            revisions[replacement] = ReadRevision(replacement);
            return edit;
        }
        catch (InvalidOperationException)
        {
            // A removed layer cannot receive a recreated object.
            return null;
        }
    }

    private Guid ResolveId(Guid objectId)
    {
        while (replacements.TryGetValue(objectId, out var replacement))
        {
            objectId = replacement;
        }
        return objectId;
    }

    private Revision GetRevision(Guid objectId)
    {
        if (!revisions.TryGetValue(objectId, out var revision))
        {
            revision = ReadRevision(objectId);
            revisions[objectId] = revision;
        }
        return revision;
    }

    private Revision ReadRevision(Guid objectId)
    {
        var engine = getEngine();
        var shape = engine.GetShapes().FirstOrDefault(item => item.ShapeId == objectId);
        var stroke = engine.GetStrokes().FirstOrDefault(item => item.StrokeId == objectId);
        var move = shape?.MovedAt ?? stroke?.MovedAt;
        var resize = shape?.ResizedAt;
        var layer = shape?.LayerChangedAt ?? stroke?.LayerChangedAt;
        // Foreign metadata must not become an accepted local revision during recording.
        if (move?.PeerId != engine.PeerId)
        {
            move = null;
        }
        if (resize?.PeerId != engine.PeerId)
        {
            resize = null;
        }
        if (layer?.PeerId != engine.PeerId)
        {
            layer = null;
        }
        return new Revision(move, resize, layer);
    }

    private enum EditKind
    {
        Creation,
        Move,
        Resize
    }
    private sealed record Revision(OperationStamp? MoveStamp, OperationStamp? ResizeStamp, OperationStamp? LayerStamp);
    private sealed record Edit(EditKind Kind, Guid ObjectId, OperationStamp SourceStamp, StrokePoint BeforeOffset = default, StrokePoint AfterOffset = default,
        Shape? BeforeShape = null, Shape? AfterShape = null, Stroke? Stroke = null);
}
