using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using P2PDraw.Core.Models;

namespace P2PDraw.App.Controls;

public enum DrawingTool
{
    Select,
    Brush,
    Line,
    Rectangle,
    Ellipse
}

public sealed class DrawingCanvas : Control
{
    public static readonly StyledProperty<IReadOnlyList<Stroke>?> StrokesProperty =
        AvaloniaProperty.Register<DrawingCanvas, IReadOnlyList<Stroke>?>(nameof(Strokes));

    public static readonly StyledProperty<IReadOnlyList<Shape>?> ShapesProperty =
        AvaloniaProperty.Register<DrawingCanvas, IReadOnlyList<Shape>?>(nameof(Shapes));
    public static readonly StyledProperty<uint> StrokeColorProperty = AvaloniaProperty.Register<DrawingCanvas, uint>(nameof(StrokeColor), 0xFF253449);
    public static readonly StyledProperty<double> StrokeWidthProperty = AvaloniaProperty.Register<DrawingCanvas, double>(nameof(StrokeWidth), 3);
    public static readonly StyledProperty<bool> FillShapesProperty = AvaloniaProperty.Register<DrawingCanvas, bool>(nameof(FillShapes));
    public static readonly StyledProperty<IReadOnlyList<Guid>?> VisibleLayerIdsProperty =
        AvaloniaProperty.Register<DrawingCanvas, IReadOnlyList<Guid>?>(nameof(VisibleLayerIds));
    public static readonly StyledProperty<IReadOnlySet<Guid>?> SelectableLayerIdsProperty =
        AvaloniaProperty.Register<DrawingCanvas, IReadOnlySet<Guid>?>(nameof(SelectableLayerIds));
    public static readonly StyledProperty<bool> CanDrawProperty = AvaloniaProperty.Register<DrawingCanvas, bool>(nameof(CanDraw), true);

    private DrawingTool tool = DrawingTool.Brush;
    private StrokePoint dragStart;
    private StrokePoint dragPosition;
    private StrokePoint initialOffset;
    private Shape? resizingShape;
    private int resizingHandle;
    private bool touchInput;
    public Guid? SelectedObjectId { get; private set; }
    public event Action<Guid?>? SelectionChanged;
    public event Action<ShapeKind, StrokePoint, StrokePoint>? ShapeCreated;
    public event Action<Guid, StrokePoint>? ObjectMoved;
    public event Action<Guid, StrokePoint, StrokePoint>? ShapeResized;
    public event Action<Guid>? ObjectDeleted;

    public IReadOnlyList<Shape>? Shapes
    {
        get => GetValue(ShapesProperty);
        set => SetValue(ShapesProperty, value);
    }

    public uint StrokeColor
    {
        get => GetValue(StrokeColorProperty);
        set => SetValue(StrokeColorProperty, value);
    }

    public double StrokeWidth
    {
        get => GetValue(StrokeWidthProperty);
        set => SetValue(StrokeWidthProperty, value);
    }

    public bool FillShapes
    {
        get => GetValue(FillShapesProperty);
        set => SetValue(FillShapesProperty, value);
    }

    public IReadOnlyList<Guid>? VisibleLayerIds
    {
        get => GetValue(VisibleLayerIdsProperty);
        set => SetValue(VisibleLayerIdsProperty, value);
    }

    public IReadOnlySet<Guid>? SelectableLayerIds
    {
        get => GetValue(SelectableLayerIdsProperty);
        set => SetValue(SelectableLayerIdsProperty, value);
    }

    public bool CanDraw
    {
        get => GetValue(CanDrawProperty);
        set => SetValue(CanDrawProperty, value);
    }

    public DrawingTool Tool
    {
        get => tool;
        set
        {
            if (tool == value)
            {
                return;
            }
            FinishStroke(false);
            tool = value;
            ClearSelection();
            Cursor = new Cursor(tool == DrawingTool.Select ? StandardCursorType.Arrow : StandardCursorType.Cross);
        }
    }

    private IPointer? drawingPointer;
    private IPointer? panPointer;
    private StrokePoint lastPoint;
    private Point lastPanPoint;
    private Point touchStart;
    private readonly Dictionary<IPointer, Point> touches = [];
    private bool strokeStarted;
    private bool navigatingTouch;
    private Point gestureCenter;
    private double gestureDistance;

    public event Action<StrokePoint>? StrokeStarted;
    public event Action<StrokePoint>? StrokeContinued;
    public event Action? StrokeFinished;
    public event Action? ViewChanged;
    public double Zoom { get; private set; } = 1;
    public Vector Pan { get; private set; }

    public IReadOnlyList<Stroke>? Strokes
    {
        get => GetValue(StrokesProperty);
        set => SetValue(StrokesProperty, value);
    }

    static DrawingCanvas()
    {
        AffectsRender<DrawingCanvas>(StrokesProperty, ShapesProperty, StrokeColorProperty, StrokeWidthProperty, FillShapesProperty, VisibleLayerIdsProperty);
    }

    public DrawingCanvas()
    {
        Focusable = true;
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Cross);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var canvasBounds = new Rect(Bounds.Size);
        context.DrawRectangle(Brushes.White, null, canvasBounds);

        using var clip = context.PushClip(canvasBounds);
        using var transform = context.PushTransform(Matrix.CreateScale(Zoom, Zoom) * Matrix.CreateTranslation(Pan));
        if (VisibleLayerIds is null)
        {
            DrawLayer(context, null);
        }
        else
        {
            foreach (var layerId in VisibleLayerIds)
            {
                DrawLayer(context, layerId);
            }
        }
        if (strokeStarted && tool != DrawingTool.Brush && tool != DrawingTool.Select)
        {
            DrawShape(context, ToolShapeKind(), dragStart, dragPosition, StrokeColor, StrokeWidth, PreviewFill());
        }
        if (SelectedObjectId is Guid selected && FindBounds(selected) is Rect bounds)
        {
            var outline = new Pen(Brushes.DodgerBlue, 1.5 / Zoom);
            context.DrawRectangle(null, outline, bounds.Inflate(5 / Zoom));
            if (FindShape(selected) is Shape selectedShape)
            {
                var displayedShape = DisplayShape(selectedShape);
                displayedShape = displayedShape with { Offset = DisplayOffset(selected, displayedShape.Offset) };
                double handleSize = (touchInput ? 20 : 10) / Zoom;
                foreach (var handle in CanvasGeometry.GetResizeHandles(displayedShape))
                {
                    var handleBounds = new Rect(handle.X - handleSize / 2, handle.Y - handleSize / 2, handleSize, handleSize);
                    context.DrawRectangle(Brushes.White, outline, handleBounds);
                }
            }
        }
    }

    private void DrawLayer(DrawingContext context, Guid? layerId)
    {
        foreach (var stroke in Strokes ?? [])
        {
            if (stroke.Points.IsDefaultOrEmpty || layerId is Guid strokeLayer && stroke.LayerId != strokeLayer)
            {
                continue;
            }
            var brush = new SolidColorBrush(Color.FromUInt32(stroke.Color));
            var offset = DisplayOffset(stroke.StrokeId, stroke.Offset);
            var firstPoint = new Point(stroke.Points[0].X + offset.X, stroke.Points[0].Y + offset.Y);
            if (stroke.Points.Length == 1)
            {
                context.DrawEllipse(brush, null, firstPoint, stroke.Width / 2, stroke.Width / 2);
                continue;
            }

            var geometry = new StreamGeometry();
            using (var path = geometry.Open())
            {
                path.BeginFigure(firstPoint, false);
                for (int i = 1; i < stroke.Points.Length; i++)
                {
                    var point = stroke.Points[i];
                    path.LineTo(new Point(point.X + offset.X, point.Y + offset.Y));
                }
                path.EndFigure(false);
            }
            var pen = new Pen(brush, stroke.Width, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
            context.DrawGeometry(null, pen, geometry);
        }
        foreach (var shape in Shapes ?? [])
        {
            if (layerId is Guid shapeLayer && shape.LayerId != shapeLayer)
            {
                continue;
            }
            var displayed = DisplayShape(shape);
            var offset = DisplayOffset(shape.ShapeId, displayed.Offset);
            DrawShape(context, displayed.Kind, new(displayed.Start.X + offset.X, displayed.Start.Y + offset.Y),
                new(displayed.End.X + offset.X, displayed.End.Y + offset.Y), displayed.Color, displayed.Width, displayed.Fill);
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs eventArgs)
    {
        base.OnPointerPressed(eventArgs);
        Focus();
        touchInput = eventArgs.Pointer.Type == PointerType.Touch;
        if (eventArgs.Pointer.Type == PointerType.Touch)
        {
            if (panPointer is not null || drawingPointer is not null && drawingPointer.Type != PointerType.Touch)
            {
                eventArgs.Handled = true;
                return;
            }
            touches[eventArgs.Pointer] = eventArgs.GetPosition(this);
            eventArgs.Pointer.Capture(this);
            if (touches.Count == 1)
            {
                drawingPointer = eventArgs.Pointer;
                touchStart = eventArgs.GetPosition(this);
                lastPoint = GetCanvasPoint(eventArgs);
                strokeStarted = false;
            }
            else if (!strokeStarted)
            {
                // Wait for one-finger movement before drawing, so a pinch starts without a stray dot.
                drawingPointer = null;
                navigatingTouch = true;
                UpdateTouchView(true);
            }
            eventArgs.Handled = true;
            return;
        }
        if (navigatingTouch || touches.Count > 0)
        {
            eventArgs.Handled = true;
            return;
        }
        if (eventArgs.GetCurrentPoint(this).Properties.IsMiddleButtonPressed && drawingPointer is null)
        {
            panPointer = eventArgs.Pointer;
            lastPanPoint = eventArgs.GetPosition(this);
            panPointer.Capture(this);
            eventArgs.Handled = true;
            return;
        }
        if (drawingPointer is not null)
        {
            eventArgs.Handled = true;
            return;
        }
        if (eventArgs.Pointer.Type == PointerType.Mouse && !eventArgs.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }
        if (!CanDraw && tool != DrawingTool.Select)
        {
            return;
        }
        drawingPointer = eventArgs.Pointer;
        drawingPointer.Capture(this);
        lastPoint = GetCanvasPoint(eventArgs);
        strokeStarted = true;
        BeginAction(lastPoint);
        eventArgs.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs eventArgs)
    {
        base.OnPointerMoved(eventArgs);
        if (eventArgs.Pointer == panPointer)
        {
            var position = eventArgs.GetPosition(this);
            PanBy(position - lastPanPoint);
            lastPanPoint = position;
            eventArgs.Handled = true;
            return;
        }
        if (touches.ContainsKey(eventArgs.Pointer))
        {
            touches[eventArgs.Pointer] = eventArgs.GetPosition(this);
            if (navigatingTouch)
            {
                UpdateTouchView(false);
                eventArgs.Handled = true;
                return;
            }
        }
        if (drawingPointer != eventArgs.Pointer)
        {
            return;
        }
        if (!strokeStarted)
        {
            if (!CanDraw && tool != DrawingTool.Select)
            {
                return;
            }
            Vector movement = eventArgs.GetPosition(this) - touchStart;
            if (movement.Length < 3)
            {
                return;
            }
            strokeStarted = true;
            BeginAction(lastPoint);
        }
        if (eventArgs.Pointer.Type == PointerType.Mouse && !eventArgs.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            FinishStroke();
            return;
        }
        var point = GetCanvasPoint(eventArgs);
        if (point != lastPoint)
        {
            lastPoint = point;
            ContinueAction(point);
        }
        eventArgs.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs eventArgs)
    {
        base.OnPointerReleased(eventArgs);
        if (eventArgs.Pointer == panPointer)
        {
            panPointer = null;
            eventArgs.Pointer.Capture(null);
            eventArgs.Handled = true;
            return;
        }
        touches.Remove(eventArgs.Pointer);
        if (navigatingTouch)
        {
            if (touches.Count == 0)
            {
                navigatingTouch = false;
            }
            eventArgs.Pointer.Capture(null);
            eventArgs.Handled = true;
            return;
        }
        if (drawingPointer != eventArgs.Pointer)
        {
            eventArgs.Pointer.Capture(null);
            return;
        }
        if (eventArgs.Pointer.Type == PointerType.Mouse && eventArgs.InitialPressMouseButton != MouseButton.Left)
        {
            return;
        }
        if (!strokeStarted)
        {
            if (!CanDraw && tool != DrawingTool.Select)
            {
                FinishStroke(false);
                eventArgs.Handled = true;
                return;
            }
            strokeStarted = true;
            BeginAction(lastPoint);
        }
        var point = GetCanvasPoint(eventArgs);
        if (point != lastPoint)
        {
            ContinueAction(point);
        }
        FinishStroke();
        eventArgs.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs eventArgs)
    {
        base.OnPointerCaptureLost(eventArgs);
        touches.Remove(eventArgs.Pointer);
        if (touches.Count == 0)
        {
            navigatingTouch = false;
        }
        if (eventArgs.Pointer == panPointer)
        {
            panPointer = null;
        }
        if (eventArgs.Pointer == drawingPointer)
        {
            FinishStroke(false);
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs eventArgs)
    {
        FinishStroke(false);
        panPointer?.Capture(null);
        panPointer = null;
        foreach (var pointer in touches.Keys.ToArray())
        {
            pointer.Capture(null);
        }
        touches.Clear();
        navigatingTouch = false;
        base.OnDetachedFromVisualTree(eventArgs);
    }

    private StrokePoint GetCanvasPoint(PointerEventArgs eventArgs)
    {
        var point = eventArgs.GetPosition(this);
        var screenPoint = new Point(Math.Clamp(point.X, 0, Bounds.Width), Math.Clamp(point.Y, 0, Bounds.Height));
        return ScreenToDocument(screenPoint);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs eventArgs)
    {
        base.OnPointerWheelChanged(eventArgs);
        if (drawingPointer is null && !navigatingTouch)
        {
            ZoomAt(Math.Pow(1.2, eventArgs.Delta.Y), eventArgs.GetPosition(this));
        }
        eventArgs.Handled = true;
    }

    public StrokePoint ScreenToDocument(Point point) => new((point.X - Pan.X) / Zoom, (point.Y - Pan.Y) / Zoom);
    public Point DocumentToScreen(StrokePoint point) => new(point.X * Zoom + Pan.X, point.Y * Zoom + Pan.Y);

    public void ZoomAt(double factor, Point anchor)
    {
        if (!double.IsFinite(factor) || factor <= 0 || !double.IsFinite(anchor.X) || !double.IsFinite(anchor.Y))
        {
            throw new ArgumentOutOfRangeException(nameof(factor));
        }
        var documentPoint = ScreenToDocument(anchor);
        double nextZoom = Math.Clamp(Zoom * factor, 0.1, 8);
        var nextPan = new Vector(anchor.X - documentPoint.X * nextZoom, anchor.Y - documentPoint.Y * nextZoom);
        SetView(nextZoom, nextPan);
    }

    public void PanBy(Vector delta) => SetView(Zoom, Pan + delta);
    public void ResetView() => SetView(1, default);

    private void SetView(double zoom, Vector pan)
    {
        if (!double.IsFinite(pan.X) || !double.IsFinite(pan.Y))
        {
            throw new ArgumentOutOfRangeException(nameof(pan));
        }
        if (Zoom == zoom && Pan == pan)
        {
            return;
        }
        Zoom = zoom;
        Pan = pan;
        InvalidateVisual();
        ViewChanged?.Invoke();
    }

    private void UpdateTouchView(bool initialize)
    {
        var positions = touches.Values.Take(2).ToArray();
        if (positions.Length < 2)
        {
            return;
        }
        var center = new Point((positions[0].X + positions[1].X) / 2, (positions[0].Y + positions[1].Y) / 2);
        Vector separation = positions[0] - positions[1];
        double distance = separation.Length;
        if (!initialize && gestureDistance > 1 && distance > 1)
        {
            var documentPoint = ScreenToDocument(gestureCenter);
            double nextZoom = Math.Clamp(Zoom * distance / gestureDistance, 0.1, 8);
            var nextPan = new Vector(center.X - documentPoint.X * nextZoom, center.Y - documentPoint.Y * nextZoom);
            SetView(nextZoom, nextPan);
        }
        gestureCenter = center;
        gestureDistance = distance;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == CanDrawProperty && !CanDraw && tool != DrawingTool.Select)
        {
            FinishStroke(false);
        }
        if ((change.Property == StrokesProperty || change.Property == ShapesProperty
            || change.Property == VisibleLayerIdsProperty || change.Property == SelectableLayerIdsProperty)
            && SelectedObjectId is Guid selected && !CanSelectObject(selected))
        {
            FinishStroke(false);
            ClearSelection();
        }
    }

    protected override void OnKeyDown(KeyEventArgs eventArgs)
    {
        base.OnKeyDown(eventArgs);
        if (eventArgs.Key == Key.Delete && SelectedObjectId is Guid selected)
        {
            ObjectDeleted?.Invoke(selected);
            ClearSelection();
            eventArgs.Handled = true;
        }
    }

    public void ClearSelection()
    {
        SelectedObjectId = null;
        resizingShape = null;
        SelectionChanged?.Invoke(null);
        InvalidateVisual();
    }

    public Guid? HitTestObject(StrokePoint point, bool touch = false)
    {
        double tolerance = (touch ? 12 : 6) / Zoom;
        if (VisibleLayerIds is null)
        {
            return HitTestLayer(point, tolerance, null);
        }
        for (int index = VisibleLayerIds.Count - 1; index >= 0; index--)
        {
            var objectId = HitTestLayer(point, tolerance, VisibleLayerIds[index]);
            if (objectId is not null)
            {
                return objectId;
            }
        }
        return null;
    }

    private Guid? HitTestLayer(StrokePoint point, double tolerance, Guid? layerId)
    {
        if (Shapes is not null)
        {
            for (int index = Shapes.Count - 1; index >= 0; index--)
            {
                var shape = Shapes[index];
                if ((layerId is null || shape.LayerId == layerId) && IsSelectableLayer(shape.LayerId)
                    && CanvasGeometry.Contains(shape, point, tolerance))
                {
                    return shape.ShapeId;
                }
            }
        }
        if (Strokes is not null)
        {
            for (int index = Strokes.Count - 1; index >= 0; index--)
            {
                var stroke = Strokes[index];
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
        return (VisibleLayerIds is null || VisibleLayerIds.Contains(layerId))
            && (SelectableLayerIds is null || SelectableLayerIds.Contains(layerId));
    }

    private bool CanSelectObject(Guid objectId)
    {
        foreach (var stroke in Strokes ?? [])
        {
            if (stroke.StrokeId == objectId)
            {
                return IsSelectableLayer(stroke.LayerId);
            }
        }
        foreach (var shape in Shapes ?? [])
        {
            if (shape.ShapeId == objectId)
            {
                return IsSelectableLayer(shape.LayerId);
            }
        }
        return false;
    }

    private void BeginAction(StrokePoint point)
    {
        dragStart = point;
        dragPosition = point;
        resizingShape = null;
        if (tool == DrawingTool.Brush)
        {
            StrokeStarted?.Invoke(point);
        }
        else if (tool == DrawingTool.Select)
        {
            if (SelectedObjectId is Guid previous && FindShape(previous) is Shape shape)
            {
                var handles = CanvasGeometry.GetResizeHandles(shape);
                double tolerance = (touchInput ? 14 : 7) / Zoom;
                for (int index = 0; index < handles.Length; index++)
                {
                    if (Math.Abs(point.X - handles[index].X) <= tolerance && Math.Abs(point.Y - handles[index].Y) <= tolerance)
                    {
                        resizingShape = shape;
                        resizingHandle = index;
                        InvalidateVisual();
                        return;
                    }
                }
            }
            SelectedObjectId = HitTestObject(point, drawingPointer?.Type == PointerType.Touch);
            initialOffset = default;
            if (SelectedObjectId is Guid selected)
            {
                initialOffset = FindOffset(selected);
            }
            SelectionChanged?.Invoke(SelectedObjectId);
        }
        InvalidateVisual();
    }

    private void ContinueAction(StrokePoint point)
    {
        dragPosition = point;
        if (tool == DrawingTool.Brush)
        {
            StrokeContinued?.Invoke(point);
        }
        InvalidateVisual();
    }

    private void EndAction(bool commit)
    {
        if (tool == DrawingTool.Brush)
        {
            StrokeFinished?.Invoke();
        }
        else if (commit && dragStart != dragPosition)
        {
            if (tool == DrawingTool.Select && SelectedObjectId is Guid selected)
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
            else if (tool != DrawingTool.Select)
            {
                ShapeCreated?.Invoke(ToolShapeKind(), dragStart, dragPosition);
            }
        }
        resizingShape = null;
        InvalidateVisual();
    }

    private Shape? FindShape(Guid objectId)
    {
        foreach (var shape in Shapes ?? [])
        {
            if (shape.ShapeId == objectId)
            {
                return shape;
            }
        }
        return null;
    }

    private Shape DisplayShape(Shape shape)
    {
        if (strokeStarted && resizingShape?.ShapeId == shape.ShapeId)
        {
            return CanvasGeometry.Resize(resizingShape, resizingHandle, dragPosition) with { Offset = shape.Offset };
        }
        return shape;
    }

    private StrokePoint FindOffset(Guid objectId)
    {
        foreach (var stroke in Strokes ?? [])
        {
            if (stroke.StrokeId == objectId)
            {
                return stroke.Offset;
            }
        }
        foreach (var shape in Shapes ?? [])
        {
            if (shape.ShapeId == objectId)
            {
                return shape.Offset;
            }
        }
        return default;
    }

    private Rect? FindBounds(Guid objectId)
    {
        foreach (var stroke in Strokes ?? [])
        {
            if (stroke.StrokeId == objectId)
            {
                return CanvasGeometry.GetBounds(stroke with { Offset = DisplayOffset(objectId, stroke.Offset) });
            }
        }
        foreach (var shape in Shapes ?? [])
        {
            if (shape.ShapeId == objectId)
            {
                var displayed = DisplayShape(shape);
                return CanvasGeometry.GetBounds(displayed with { Offset = DisplayOffset(objectId, displayed.Offset) });
            }
        }
        return null;
    }

    private StrokePoint DisplayOffset(Guid objectId, StrokePoint offset)
    {
        if (tool == DrawingTool.Select && strokeStarted && resizingShape is null && SelectedObjectId == objectId)
        {
            return new(initialOffset.X + dragPosition.X - dragStart.X, initialOffset.Y + dragPosition.Y - dragStart.Y);
        }
        return offset;
    }

    private ShapeKind ToolShapeKind() => tool switch
    {
        DrawingTool.Line => ShapeKind.Line,
        DrawingTool.Rectangle => ShapeKind.Rectangle,
        DrawingTool.Ellipse => ShapeKind.Ellipse,
        _ => throw new InvalidOperationException("Choose a shape tool first.")
    };

    private uint? PreviewFill() => FillShapes && tool != DrawingTool.Line ? StrokeColor : null;

    private static void DrawShape(DrawingContext context, ShapeKind kind, StrokePoint start, StrokePoint end, uint color, double width, uint? fill)
    {
        var pen = new Pen(new SolidColorBrush(Color.FromUInt32(color)), width);
        var bounds = new Rect(Math.Min(start.X, end.X), Math.Min(start.Y, end.Y), Math.Abs(end.X - start.X), Math.Abs(end.Y - start.Y));
        SolidColorBrush? fillBrush = null;
        if (fill is uint fillColor)
        {
            fillBrush = new SolidColorBrush(Color.FromUInt32(fillColor));
        }
        switch (kind)
        {
            case ShapeKind.Line:
                context.DrawLine(pen, new Point(start.X, start.Y), new Point(end.X, end.Y));
                break;
            case ShapeKind.Rectangle:
                context.DrawRectangle(fillBrush, pen, bounds);
                break;
            case ShapeKind.Ellipse:
                context.DrawEllipse(fillBrush, pen, bounds);
                break;
        }
    }

    private void FinishStroke(bool commit = true)
    {
        if (drawingPointer is null)
        {
            return;
        }
        var pointer = drawingPointer;
        drawingPointer = null;
        bool notify = strokeStarted;
        strokeStarted = false;
        if (notify)
        {
            EndAction(commit);
        }
        pointer.Capture(null);
    }
}
