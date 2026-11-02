using P2PDraw.Core.Models;
using P2PDraw.Core.Operations;
using P2PDraw.Core.Sync;

namespace P2PDraw.App.Editing;

internal sealed class ObjectDuplicator(Func<SyncEngine> getEngine, Action<Operation> publish)
{
    public Guid Copy(Shape shape, StrokePoint displacement)
    {
        var engine = getEngine();
        var added = engine.AddShape(shape.Kind, shape.Start, shape.End, shape.Color, shape.Width, shape.Fill, shape.LayerId);
        publish(added);
        var offset = new StrokePoint(shape.Offset.X + displacement.X, shape.Offset.Y + displacement.Y);
        if (offset != default)
        {
            publish(engine.MoveObject(added.ShapeId, offset));
        }
        return added.ShapeId;
    }

    public Guid Copy(Stroke stroke, StrokePoint displacement)
    {
        var engine = getEngine();
        var added = engine.BeginStroke(stroke.Color, stroke.Width, stroke.LayerId);
        publish(added);
        if (!stroke.Points.IsDefaultOrEmpty)
        {
            publish(engine.AppendStroke(added.StrokeId, stroke.Points));
        }
        publish(engine.EndStroke(added.StrokeId));
        var offset = new StrokePoint(stroke.Offset.X + displacement.X, stroke.Offset.Y + displacement.Y);
        if (offset != default)
        {
            publish(engine.MoveObject(added.StrokeId, offset));
        }
        return added.StrokeId;
    }
}
