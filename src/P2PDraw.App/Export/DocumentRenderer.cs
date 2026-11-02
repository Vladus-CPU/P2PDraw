using Avalonia;
using Avalonia.Media;
using P2PDraw.Core.Models;

namespace P2PDraw.App.Export;

public static class DocumentRenderer
{
    public static void DrawStroke(DrawingContext context, Stroke stroke, StrokePoint offset)
    {
        if (stroke.Points.IsDefaultOrEmpty)
        {
            return;
        }
        var brush = new SolidColorBrush(Color.FromUInt32(stroke.Color));
        var firstPoint = new Point(stroke.Points[0].X + offset.X, stroke.Points[0].Y + offset.Y);
        if (stroke.Points.Length == 1)
        {
            context.DrawEllipse(brush, null, firstPoint, stroke.Width / 2, stroke.Width / 2);
            return;
        }
        var geometry = new StreamGeometry();
        using (var path = geometry.Open())
        {
            path.BeginFigure(firstPoint, false);
            for (int index = 1; index < stroke.Points.Length; index++)
            {
                var point = stroke.Points[index];
                path.LineTo(new Point(point.X + offset.X, point.Y + offset.Y));
            }
            path.EndFigure(false);
        }
        var pen = new Pen(brush, stroke.Width, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        context.DrawGeometry(null, pen, geometry);
    }

    public static void DrawShape(DrawingContext context, Shape shape)
    {
        var start = new StrokePoint(shape.Start.X + shape.Offset.X, shape.Start.Y + shape.Offset.Y);
        var end = new StrokePoint(shape.End.X + shape.Offset.X, shape.End.Y + shape.Offset.Y);
        DrawShape(context, shape.Kind, start, end, shape.Color, shape.Width, shape.Fill);
    }

    public static void DrawShape(DrawingContext context, ShapeKind kind, StrokePoint start, StrokePoint end, uint color, double width, uint? fill)
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
}
