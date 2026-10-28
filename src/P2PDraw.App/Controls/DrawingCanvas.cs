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
    private StrokePoint lastPoint;

    public event Action<StrokePoint>? StrokeStarted;
    public event Action<StrokePoint>? StrokeContinued;
    public event Action? StrokeFinished;

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
        StrokeStarted?.Invoke(lastPoint);
        eventArgs.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs eventArgs)
    {
        base.OnPointerMoved(eventArgs);
        if (drawingPointer != eventArgs.Pointer)
        {
            return;
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
        if (drawingPointer != eventArgs.Pointer)
        {
            return;
        }
        if (eventArgs.Pointer.Type == PointerType.Mouse && eventArgs.InitialPressMouseButton != MouseButton.Left)
        {
            return;
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
        if (eventArgs.Pointer == drawingPointer)
        {
            FinishStroke();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs eventArgs)
    {
        FinishStroke();
        base.OnDetachedFromVisualTree(eventArgs);
    }

    private StrokePoint GetCanvasPoint(PointerEventArgs eventArgs)
    {
        var point = eventArgs.GetPosition(this);
        return new StrokePoint(Math.Clamp(point.X, 0, Bounds.Width), Math.Clamp(point.Y, 0, Bounds.Height));
    }

    private void FinishStroke()
    {
        if (drawingPointer is null)
        {
            return;
        }
        var pointer = drawingPointer;
        drawingPointer = null;
        StrokeFinished?.Invoke();
        pointer.Capture(null);
    }
}
