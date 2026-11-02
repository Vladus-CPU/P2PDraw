using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using P2PDraw.Core.Models;

namespace P2PDraw.App.Controls;

public sealed class DrawingCanvas : Control
{
    public static readonly StyledProperty<IReadOnlyList<Stroke>?> StrokesProperty =
        AvaloniaProperty.Register<DrawingCanvas, IReadOnlyList<Stroke>?>(nameof(Strokes));

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
        AffectsRender<DrawingCanvas>(StrokesProperty);
    }

    public DrawingCanvas()
    {
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Cross);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var canvasBounds = new Rect(Bounds.Size);
        context.DrawRectangle(Brushes.White, null, canvasBounds);
        if (Strokes is null)
        {
            return;
        }

        using var clip = context.PushClip(canvasBounds);
        using var transform = context.PushTransform(Matrix.CreateScale(Zoom, Zoom) * Matrix.CreateTranslation(Pan));
        foreach (var stroke in Strokes)
        {
            if (stroke.Points.IsDefaultOrEmpty)
            {
                continue;
            }
            var brush = new SolidColorBrush(Color.FromUInt32(stroke.Color));
            var firstPoint = new Point(stroke.Points[0].X, stroke.Points[0].Y);
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
                    path.LineTo(new Point(point.X, point.Y));
                }
                path.EndFigure(false);
            }
            var pen = new Pen(brush, stroke.Width, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
            context.DrawGeometry(null, pen, geometry);
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs eventArgs)
    {
        base.OnPointerPressed(eventArgs);
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
        drawingPointer = eventArgs.Pointer;
        drawingPointer.Capture(this);
        lastPoint = GetCanvasPoint(eventArgs);
        strokeStarted = true;
        StrokeStarted?.Invoke(lastPoint);
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
            Vector movement = eventArgs.GetPosition(this) - touchStart;
            if (movement.Length < 3)
            {
                return;
            }
            strokeStarted = true;
            StrokeStarted?.Invoke(lastPoint);
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
            StrokeContinued?.Invoke(point);
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
            strokeStarted = true;
            StrokeStarted?.Invoke(lastPoint);
        }
        var point = GetCanvasPoint(eventArgs);
        if (point != lastPoint)
        {
            StrokeContinued?.Invoke(point);
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
            FinishStroke();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs eventArgs)
    {
        FinishStroke();
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

    private void FinishStroke()
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
            StrokeFinished?.Invoke();
        }
        pointer.Capture(null);
    }
}
