using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using P2PDraw.App.Controls;
using P2PDraw.Core.Models;

namespace P2PDraw.App.Interaction;

internal sealed class CanvasPointerInput
{
    private readonly Control target;
    private readonly CanvasInteraction interaction;
    private readonly CanvasViewport viewport;
    private readonly Dictionary<IPointer, Point> touches = [];
    private IPointer? drawingPointer;
    private IPointer? panPointer;
    private StrokePoint lastPoint;
    private Point lastPanPoint;
    private Point touchStart;
    private bool navigatingTouch;

    public bool CanDraw { get; set; } = true;
    public bool TouchInput { get; private set; }

    public CanvasPointerInput(Control target, CanvasInteraction interaction, CanvasViewport viewport)
    {
        this.target = target;
        this.interaction = interaction;
        this.viewport = viewport;
    }

    public void PointerPressed(PointerPressedEventArgs eventArgs)
    {
        target.Focus();
        TouchInput = eventArgs.Pointer.Type == PointerType.Touch;
        if (eventArgs.Pointer.Type == PointerType.Touch)
        {
            if (panPointer is not null || drawingPointer is not null && drawingPointer.Type != PointerType.Touch)
            {
                eventArgs.Handled = true;
                return;
            }
            touches[eventArgs.Pointer] = eventArgs.GetPosition(target);
            eventArgs.Pointer.Capture(target);
            if (touches.Count == 1)
            {
                drawingPointer = eventArgs.Pointer;
                touchStart = eventArgs.GetPosition(target);
                lastPoint = GetCanvasPoint(eventArgs);
            }
            else if (!interaction.IsActive)
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
        if (eventArgs.GetCurrentPoint(target).Properties.IsMiddleButtonPressed && drawingPointer is null)
        {
            panPointer = eventArgs.Pointer;
            lastPanPoint = eventArgs.GetPosition(target);
            panPointer.Capture(target);
            eventArgs.Handled = true;
            return;
        }
        if (drawingPointer is not null)
        {
            eventArgs.Handled = true;
            return;
        }
        if (eventArgs.Pointer.Type == PointerType.Mouse && !eventArgs.GetCurrentPoint(target).Properties.IsLeftButtonPressed)
        {
            return;
        }
        if (!CanDraw && interaction.Tool != DrawingTool.Select)
        {
            return;
        }
        drawingPointer = eventArgs.Pointer;
        drawingPointer.Capture(target);
        lastPoint = GetCanvasPoint(eventArgs);
        BeginAction();
        eventArgs.Handled = true;
    }

    public void PointerMoved(PointerEventArgs eventArgs)
    {
        if (eventArgs.Pointer == panPointer)
        {
            var position = eventArgs.GetPosition(target);
            viewport.PanBy(position - lastPanPoint);
            lastPanPoint = position;
            eventArgs.Handled = true;
            return;
        }
        if (touches.ContainsKey(eventArgs.Pointer))
        {
            touches[eventArgs.Pointer] = eventArgs.GetPosition(target);
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
        if (!interaction.IsActive)
        {
            if (!CanDraw && interaction.Tool != DrawingTool.Select)
            {
                return;
            }
            Vector movement = eventArgs.GetPosition(target) - touchStart;
            if (movement.Length < 3)
            {
                return;
            }
            BeginAction();
        }
        if (eventArgs.Pointer.Type == PointerType.Mouse && !eventArgs.GetCurrentPoint(target).Properties.IsLeftButtonPressed)
        {
            FinishStroke();
            return;
        }
        var point = GetCanvasPoint(eventArgs);
        if (point != lastPoint)
        {
            lastPoint = point;
            interaction.Continue(point);
        }
        eventArgs.Handled = true;
    }

    public void PointerReleased(PointerReleasedEventArgs eventArgs)
    {
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
        if (!interaction.IsActive)
        {
            if (!CanDraw && interaction.Tool != DrawingTool.Select)
            {
                FinishStroke(false);
                eventArgs.Handled = true;
                return;
            }
            BeginAction();
        }
        var point = GetCanvasPoint(eventArgs);
        if (point != lastPoint)
        {
            interaction.Continue(point);
        }
        FinishStroke();
        eventArgs.Handled = true;
    }

    public void PointerCaptureLost(PointerCaptureLostEventArgs eventArgs)
    {
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

    public void Detach()
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
    }

    private void BeginAction()
    {
        double selectionTolerance = (drawingPointer?.Type == PointerType.Touch ? 12 : 6) / viewport.Zoom;
        double resizeTolerance = (TouchInput ? 14 : 7) / viewport.Zoom;
        interaction.Begin(lastPoint, selectionTolerance, resizeTolerance);
    }

    private StrokePoint GetCanvasPoint(PointerEventArgs eventArgs)
    {
        var point = eventArgs.GetPosition(target);
        var screenPoint = new Point(Math.Clamp(point.X, 0, target.Bounds.Width), Math.Clamp(point.Y, 0, target.Bounds.Height));
        return viewport.ScreenToDocument(screenPoint);
    }

    public void PointerWheelChanged(PointerWheelEventArgs eventArgs)
    {
        if (drawingPointer is null && !navigatingTouch)
        {
            viewport.ZoomAt(Math.Pow(1.2, eventArgs.Delta.Y), eventArgs.GetPosition(target));
        }
        eventArgs.Handled = true;
    }

    private void UpdateTouchView(bool initialize)
    {
        var positions = touches.Values.Take(2).ToArray();
        if (positions.Length < 2)
        {
            return;
        }
        viewport.UpdateTouchGesture(positions[0], positions[1], initialize);
    }

    public void FinishStroke(bool commit = true)
    {
        if (drawingPointer is null)
        {
            return;
        }
        var pointer = drawingPointer;
        drawingPointer = null;
        if (interaction.IsActive)
        {
            interaction.Finish(commit);
        }
        pointer.Capture(null);
    }
}
