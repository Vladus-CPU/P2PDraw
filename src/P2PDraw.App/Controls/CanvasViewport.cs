using Avalonia;
using P2PDraw.Core.Models;

namespace P2PDraw.App.Controls;

internal sealed class CanvasViewport
{
    private Point gestureCenter;
    private double gestureDistance;

    public event Action? Changed;
    public double Zoom { get; private set; } = 1;
    public Vector Pan { get; private set; }

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

    public void UpdateTouchGesture(Point first, Point second, bool initialize)
    {
        var center = new Point((first.X + second.X) / 2, (first.Y + second.Y) / 2);
        Vector separation = first - second;
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
        Changed?.Invoke();
    }
}
