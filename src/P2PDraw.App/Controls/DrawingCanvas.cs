using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using P2PDraw.App.Export;
using P2PDraw.App.Interaction;
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

    private readonly CanvasInteraction interaction = new();
    private readonly CanvasPointerInput input;
    public Guid? SelectedObjectId => interaction.SelectedObjectId;
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
        get => interaction.Tool;
        set
        {
            if (Tool == value)
            {
                return;
            }
            input.FinishStroke(false);
            interaction.ChangeTool(value);
            Cursor = new Cursor(Tool == DrawingTool.Select ? StandardCursorType.Arrow : StandardCursorType.Cross);
        }
    }

    private readonly CanvasViewport viewport = new();

    public event Action<StrokePoint>? StrokeStarted;
    public event Action<StrokePoint>? StrokeContinued;
    public event Action? StrokeFinished;
    public event Action? ViewChanged;
    public double Zoom => viewport.Zoom;
    public Vector Pan => viewport.Pan;

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
        input = new CanvasPointerInput(this, interaction, viewport);
        interaction.Changed += InvalidateVisual;
        interaction.SelectionChanged += selected => SelectionChanged?.Invoke(selected);
        interaction.StrokeStarted += point => StrokeStarted?.Invoke(point);
        interaction.StrokeContinued += point => StrokeContinued?.Invoke(point);
        interaction.StrokeFinished += () => StrokeFinished?.Invoke();
        interaction.ShapeCreated += (kind, start, end) => ShapeCreated?.Invoke(kind, start, end);
        interaction.ObjectMoved += (objectId, offset) => ObjectMoved?.Invoke(objectId, offset);
        interaction.ShapeResized += (objectId, start, end) => ShapeResized?.Invoke(objectId, start, end);
        Focusable = true;
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Cross);
        viewport.Changed += () =>
        {
            InvalidateVisual();
            ViewChanged?.Invoke();
        };
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
        if (interaction.IsActive && Tool != DrawingTool.Brush && Tool != DrawingTool.Select)
        {
            DocumentRenderer.DrawShape(context, interaction.ToolShapeKind(), interaction.Start, interaction.Position, StrokeColor, StrokeWidth, PreviewFill());
        }
        if (SelectedObjectId is Guid selected && interaction.FindBounds(selected) is Rect bounds)
        {
            var outline = new Pen(Brushes.DodgerBlue, 1.5 / Zoom);
            context.DrawRectangle(null, outline, bounds.Inflate(5 / Zoom));
            if (interaction.FindShape(selected) is Shape selectedShape)
            {
                var displayedShape = interaction.DisplayShape(selectedShape);
                displayedShape = displayedShape with { Offset = interaction.DisplayOffset(selected, displayedShape.Offset) };
                double handleSize = (input.TouchInput ? 20 : 10) / Zoom;
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
            var offset = interaction.DisplayOffset(stroke.StrokeId, stroke.Offset);
            DocumentRenderer.DrawStroke(context, stroke, offset);
        }
        foreach (var shape in Shapes ?? [])
        {
            if (layerId is Guid shapeLayer && shape.LayerId != shapeLayer)
            {
                continue;
            }
            var displayed = interaction.DisplayShape(shape);
            var offset = interaction.DisplayOffset(shape.ShapeId, displayed.Offset);
            DocumentRenderer.DrawShape(context, displayed with { Offset = offset });
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs eventArgs)
    {
        base.OnPointerPressed(eventArgs);
        input.PointerPressed(eventArgs);
    }

    protected override void OnPointerMoved(PointerEventArgs eventArgs)
    {
        base.OnPointerMoved(eventArgs);
        input.PointerMoved(eventArgs);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs eventArgs)
    {
        base.OnPointerReleased(eventArgs);
        input.PointerReleased(eventArgs);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs eventArgs)
    {
        base.OnPointerCaptureLost(eventArgs);
        input.PointerCaptureLost(eventArgs);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs eventArgs)
    {
        base.OnPointerWheelChanged(eventArgs);
        input.PointerWheelChanged(eventArgs);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs eventArgs)
    {
        input.Detach();
        base.OnDetachedFromVisualTree(eventArgs);
    }

    public StrokePoint ScreenToDocument(Point point) => viewport.ScreenToDocument(point);
    public Point DocumentToScreen(StrokePoint point) => viewport.DocumentToScreen(point);
    public void ZoomAt(double factor, Point anchor) => viewport.ZoomAt(factor, anchor);
    public void PanBy(Vector delta) => viewport.PanBy(delta);
    public void ResetView() => viewport.ResetView();
    public void ClearSelection() => interaction.ClearSelection();
    public Guid? HitTestObject(StrokePoint point, bool touch = false) => interaction.HitTestObject(point, (touch ? 12 : 6) / Zoom);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == StrokesProperty || change.Property == ShapesProperty
            || change.Property == VisibleLayerIdsProperty || change.Property == SelectableLayerIdsProperty)
        {
            interaction.UpdateDocument(Strokes, Shapes, VisibleLayerIds, SelectableLayerIds);
        }
        if (change.Property == CanDrawProperty)
        {
            input.CanDraw = CanDraw;
        }
        if (change.Property == CanDrawProperty && !CanDraw && Tool != DrawingTool.Select)
        {
            input.FinishStroke(false);
        }
        if ((change.Property == StrokesProperty || change.Property == ShapesProperty
            || change.Property == VisibleLayerIdsProperty || change.Property == SelectableLayerIdsProperty)
            && SelectedObjectId is Guid selected && !interaction.CanSelectObject(selected))
        {
            input.FinishStroke(false);
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

    private uint? PreviewFill() => FillShapes && Tool != DrawingTool.Line ? StrokeColor : null;
}
