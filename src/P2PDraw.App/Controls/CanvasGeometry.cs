using Avalonia;
using P2PDraw.Core.Models;

namespace P2PDraw.App.Controls;

public static class CanvasGeometry
{
    public static StrokePoint[] GetResizeHandles(Shape shape)
    {
        if (shape.Kind == ShapeKind.Line)
        {
            return
            [
                new(shape.Start.X + shape.Offset.X, shape.Start.Y + shape.Offset.Y),
                new(shape.End.X + shape.Offset.X, shape.End.Y + shape.Offset.Y)
            ];
        }
        var bounds = GetBounds(shape);
        return
        [
            new(bounds.Left, bounds.Top), new(bounds.Right, bounds.Top),
            new(bounds.Right, bounds.Bottom), new(bounds.Left, bounds.Bottom)
        ];
    }

    public static Shape Resize(Shape shape, int handleIndex, StrokePoint position)
    {
        var localPosition = new StrokePoint(position.X - shape.Offset.X, position.Y - shape.Offset.Y);
        if (shape.Kind == ShapeKind.Line)
        {
            return handleIndex == 0 ? shape with { Start = localPosition } : shape with { End = localPosition };
        }
        var opposite = GetResizeHandles(shape)[(handleIndex + 2) % 4];
        var localOpposite = new StrokePoint(opposite.X - shape.Offset.X, opposite.Y - shape.Offset.Y);
        return shape with { Start = localPosition, End = localOpposite };
    }

    public static Rect GetBounds(Shape shape)
    {
        double left = Math.Min(shape.Start.X, shape.End.X) + shape.Offset.X;
        double top = Math.Min(shape.Start.Y, shape.End.Y) + shape.Offset.Y;
        return new Rect(left, top, Math.Abs(shape.End.X - shape.Start.X), Math.Abs(shape.End.Y - shape.Start.Y));
    }

    public static Rect GetBounds(Stroke stroke)
    {
        if (stroke.Points.IsDefaultOrEmpty)
        {
            return default;
        }
        double left = stroke.Points[0].X;
        double top = stroke.Points[0].Y;
        double right = left;
        double bottom = top;
        foreach (var point in stroke.Points)
        {
            left = Math.Min(left, point.X);
            top = Math.Min(top, point.Y);
            right = Math.Max(right, point.X);
            bottom = Math.Max(bottom, point.Y);
        }
        return new Rect(left + stroke.Offset.X, top + stroke.Offset.Y, right - left, bottom - top);
    }

    public static bool Contains(Stroke stroke, StrokePoint point, double tolerance)
    {
        if (stroke.Points.IsDefaultOrEmpty)
        {
            return false;
        }
        var local = new StrokePoint(point.X - stroke.Offset.X, point.Y - stroke.Offset.Y);
        double distance = tolerance + stroke.Width / 2;
        if (stroke.Points.Length == 1)
        {
            return SegmentDistance(local, stroke.Points[0], stroke.Points[0]) <= distance;
        }
        for (int index = 1; index < stroke.Points.Length; index++)
        {
            if (SegmentDistance(local, stroke.Points[index - 1], stroke.Points[index]) <= distance)
            {
                return true;
            }
        }
        return false;
    }

    public static bool Contains(Shape shape, StrokePoint point, double tolerance)
    {
        double distance = tolerance + shape.Width / 2;
        var local = new StrokePoint(point.X - shape.Offset.X, point.Y - shape.Offset.Y);
        if (shape.Kind == ShapeKind.Line)
        {
            return SegmentDistance(local, shape.Start, shape.End) <= distance;
        }
        var bounds = GetBounds(shape);
        var position = new Point(point.X, point.Y);
        if (shape.Kind == ShapeKind.Rectangle)
        {
            if (!bounds.Inflate(distance).Contains(position))
            {
                return false;
            }
            return shape.Fill is not null || bounds.Width <= distance * 2 || bounds.Height <= distance * 2
                || !bounds.Deflate(distance).Contains(position);
        }
        double radiusX = bounds.Width / 2;
        double radiusY = bounds.Height / 2;
        double x = point.X - bounds.Center.X;
        double y = point.Y - bounds.Center.Y;
        double outer = x * x / Math.Pow(radiusX + distance, 2) + y * y / Math.Pow(radiusY + distance, 2);
        if (outer > 1)
        {
            return false;
        }
        if (shape.Fill is not null || radiusX <= distance || radiusY <= distance)
        {
            return true;
        }
        double inner = x * x / Math.Pow(radiusX - distance, 2) + y * y / Math.Pow(radiusY - distance, 2);
        return inner >= 1;
    }

    private static double SegmentDistance(StrokePoint point, StrokePoint start, StrokePoint end)
    {
        double deltaX = end.X - start.X;
        double deltaY = end.Y - start.Y;
        double lengthSquared = deltaX * deltaX + deltaY * deltaY;
        double fraction = 0;
        if (lengthSquared > 0)
        {
            fraction = Math.Clamp(((point.X - start.X) * deltaX + (point.Y - start.Y) * deltaY) / lengthSquared, 0, 1);
        }
        double x = point.X - start.X - fraction * deltaX;
        double y = point.Y - start.Y - fraction * deltaY;
        return Math.Sqrt(x * x + y * y);
    }
}
