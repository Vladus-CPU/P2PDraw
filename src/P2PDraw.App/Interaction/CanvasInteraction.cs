using Avalonia;
using P2PDraw.App.Controls;
using P2PDraw.Core.Models;

namespace P2PDraw.App.Interaction;

public sealed class CanvasInteraction
{
    private IReadOnlyList<Stroke>? strokes;
    private IReadOnlyList<Shape>? shapes;
    private IReadOnlyList<Guid>? visibleLayerIds;
    private IReadOnlySet<Guid>? selectableLayerIds;
    private StrokePoint dragStart;
    private StrokePoint dragPosition;
    private StrokePoint initialOffset;
    private Shape? resizingShape;
    private int resizingHandle;

    public event Action? Changed;
    public event Action<Guid?>? SelectionChanged;
    public event Action<StrokePoint>? StrokeStarted;
    public event Action<StrokePoint>? StrokeContinued;
    public event Action? StrokeFinished;
    public event Action<ShapeKind, StrokePoint, StrokePoint>? ShapeCreated;
    public event Action<Guid, StrokePoint>? ObjectMoved;
    public event Action<Guid, StrokePoint, StrokePoint>? ShapeResized;
    public Guid? SelectedObjectId { get; private set; }
    public DrawingTool Tool { get; private set; } = DrawingTool.Brush;
    public bool IsActive { get; private set; }
    public StrokePoint Start => dragStart;
    public StrokePoint Position => dragPosition;

    public void UpdateDocument(IReadOnlyList<Stroke>? strokes, IReadOnlyList<Shape>? shapes,
        IReadOnlyList<Guid>? visibleLayerIds, IReadOnlySet<Guid>? selectableLayerIds)
    {
        this.strokes = strokes;
        this.shapes = shapes;
        this.visibleLayerIds = visibleLayerIds;
        this.selectableLayerIds = selectableLayerIds;
    }

    public void ChangeTool(DrawingTool tool)
    {
        if (Tool == tool)
        {
            return;
        }
        Finish(false);
        Tool = tool;
        ClearSelection();
    }

    public void ClearSelection()
    {
        SelectedObjectId = null;
        resizingShape = null;
        SelectionChanged?.Invoke(null);
        Changed?.Invoke();
    }

    public Guid? HitTestObject(StrokePoint point, double tolerance)
    {
        if (visibleLayerIds is null)
        {
            return HitTestLayer(point, tolerance, null);
        }
        for (int index = visibleLayerIds.Count - 1; index >= 0; index--)
        {
            var objectId = HitTestLayer(point, tolerance, visibleLayerIds[index]);
            if (objectId is not null)
            {
                return objectId;
            }
        }
        return null;
    }

    private Guid? HitTestLayer(StrokePoint point, double tolerance, Guid? layerId)
    {
        if (shapes is not null)
        {
            for (int index = shapes.Count - 1; index >= 0; index--)
            {
                var shape = shapes[index];
                if ((layerId is null || shape.LayerId == layerId) && IsSelectableLayer(shape.LayerId)
                    && CanvasGeometry.Contains(shape, point, tolerance))
                {
                    return shape.ShapeId;
                }
            }
        }
        if (strokes is not null)
        {
            for (int index = strokes.Count - 1; index >= 0; index--)
            {
                var stroke = strokes[index];
                if ((layerId is null || stroke.LayerId == layerId) && IsSelectableLayer(stroke.LayerId)
                    && CanvasGeometry.Contains(stroke, point, tolerance))
                {
                    return stroke.StrokeId;
                }
            }
        }
        return null;
    }

    private bool IsSelectableLayer(Guid layerId)
    {
        return (visibleLayerIds is null || visibleLayerIds.Contains(layerId))
            && (selectableLayerIds is null || selectableLayerIds.Contains(layerId));
    }

    public bool CanSelectObject(Guid objectId)
    {
        foreach (var stroke in strokes ?? [])
        {
            if (stroke.StrokeId == objectId)
            {
                return IsSelectableLayer(stroke.LayerId);
            }
        }
        foreach (var shape in shapes ?? [])
        {
            if (shape.ShapeId == objectId)
            {
                return IsSelectableLayer(shape.LayerId);
            }
        }
        return false;
    }

    public void Begin(StrokePoint point, double selectionTolerance, double resizeTolerance)
    {
        IsActive = true;
        dragStart = point;
        dragPosition = point;
        resizingShape = null;
        if (Tool == DrawingTool.Brush)
        {
            StrokeStarted?.Invoke(point);
        }
        else if (Tool == DrawingTool.Select)
        {
            if (SelectedObjectId is Guid previous && FindShape(previous) is Shape shape)
            {
                var handles = CanvasGeometry.GetResizeHandles(shape);
                for (int index = 0; index < handles.Length; index++)
                {
                    if (Math.Abs(point.X - handles[index].X) <= resizeTolerance && Math.Abs(point.Y - handles[index].Y) <= resizeTolerance)
                    {
                        resizingShape = shape;
                        resizingHandle = index;
                        Changed?.Invoke();
                        return;
                    }
                }
            }
            SelectedObjectId = HitTestObject(point, selectionTolerance);
            initialOffset = default;
            if (SelectedObjectId is Guid selected)
            {
                initialOffset = FindOffset(selected);
            }
            SelectionChanged?.Invoke(SelectedObjectId);
        }
        Changed?.Invoke();
    }

    public void Continue(StrokePoint point)
    {
        dragPosition = point;
        if (Tool == DrawingTool.Brush)
        {
            StrokeContinued?.Invoke(point);
        }
        Changed?.Invoke();
    }

    public void Finish(bool commit)
    {
        if (!IsActive)
        {
            return;
        }
        IsActive = false;
        if (Tool == DrawingTool.Brush)
        {
            StrokeFinished?.Invoke();
        }
        else if (commit && dragStart != dragPosition)
        {
            if (Tool == DrawingTool.Select && SelectedObjectId is Guid selected)
            {
                if (resizingShape is Shape original)
                {
                    var resized = CanvasGeometry.Resize(original, resizingHandle, dragPosition);
                    ShapeResized?.Invoke(selected, resized.Start, resized.End);
                }
                else
                {
                    var offset = new StrokePoint(initialOffset.X + dragPosition.X - dragStart.X, initialOffset.Y + dragPosition.Y - dragStart.Y);
                    ObjectMoved?.Invoke(selected, offset);
                }
            }
            else if (Tool != DrawingTool.Select)
            {
                ShapeCreated?.Invoke(ToolShapeKind(), dragStart, dragPosition);
            }
        }
        resizingShape = null;
        Changed?.Invoke();
    }

    public Shape? FindShape(Guid objectId)
    {
        foreach (var shape in shapes ?? [])
        {
            if (shape.ShapeId == objectId)
            {
                return shape;
            }
        }
        return null;
    }

    public Shape DisplayShape(Shape shape)
    {
        if (IsActive && resizingShape?.ShapeId == shape.ShapeId)
        {
            return CanvasGeometry.Resize(resizingShape, resizingHandle, dragPosition) with { Offset = shape.Offset };
        }
        return shape;
    }

    private StrokePoint FindOffset(Guid objectId)
    {
        foreach (var stroke in strokes ?? [])
        {
            if (stroke.StrokeId == objectId)
            {
                return stroke.Offset;
            }
        }
        foreach (var shape in shapes ?? [])
        {
            if (shape.ShapeId == objectId)
            {
                return shape.Offset;
            }
        }
        return default;
    }

    public Rect? FindBounds(Guid objectId)
    {
        foreach (var stroke in strokes ?? [])
        {
            if (stroke.StrokeId == objectId)
            {
                return CanvasGeometry.GetBounds(stroke with { Offset = DisplayOffset(objectId, stroke.Offset) });
            }
        }
        foreach (var shape in shapes ?? [])
        {
            if (shape.ShapeId == objectId)
            {
                var displayed = DisplayShape(shape);
                return CanvasGeometry.GetBounds(displayed with { Offset = DisplayOffset(objectId, displayed.Offset) });
            }
        }
        return null;
    }

    public StrokePoint DisplayOffset(Guid objectId, StrokePoint offset)
    {
        if (Tool == DrawingTool.Select && IsActive && resizingShape is null && SelectedObjectId == objectId)
        {
            return new(initialOffset.X + dragPosition.X - dragStart.X, initialOffset.Y + dragPosition.Y - dragStart.Y);
        }
        return offset;
    }

    public ShapeKind ToolShapeKind() => Tool switch
    {
        DrawingTool.Line => ShapeKind.Line,
        DrawingTool.Rectangle => ShapeKind.Rectangle,
        DrawingTool.Ellipse => ShapeKind.Ellipse,
        _ => throw new InvalidOperationException("Choose a shape tool first.")
    };
}
