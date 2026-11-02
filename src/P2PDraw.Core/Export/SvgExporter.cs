using System.Globalization;
using System.Text;
using System.Xml.Linq;
using P2PDraw.Core.Models;
using P2PDraw.Core.Sync;

namespace P2PDraw.Core.Export;

public readonly record struct ExportBounds(double X, double Y, double Width, double Height);

public static class SvgExporter
{
    private static readonly XNamespace Svg = "http://www.w3.org/2000/svg";

    public static string Export(Snapshot snapshot, IReadOnlySet<Guid>? includedLayerIds = null)
    {
        var bounds = GetBounds(snapshot, includedLayerIds);
        var root = new XElement(Svg + "svg",
            new XAttribute("viewBox", $"{Number(bounds.X)} {Number(bounds.Y)} {Number(bounds.Width)} {Number(bounds.Height)}"),
            new XAttribute("width", Number(bounds.Width)), new XAttribute("height", Number(bounds.Height)));

        foreach (var layer in snapshot.Layers)
        {
            if (includedLayerIds is not null && !includedLayerIds.Contains(layer.LayerId))
            {
                continue;
            }
            var group = new XElement(Svg + "g", new XAttribute("id", $"layer-{layer.LayerId}"), new XAttribute("data-name", layer.Name));
            foreach (var stroke in snapshot.Strokes)
            {
                if (stroke.LayerId == layer.LayerId && !stroke.Points.IsEmpty)
                {
                    group.Add(CreateStroke(stroke));
                }
            }
            foreach (var shape in snapshot.Shapes)
            {
                if (shape.LayerId == layer.LayerId)
                {
                    group.Add(CreateShape(shape));
                }
            }
            root.Add(group);
        }
        return root.ToString();
    }

    public static ExportBounds GetBounds(Snapshot snapshot, IReadOnlySet<Guid>? includedLayerIds = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Strokes.IsDefault || snapshot.Shapes.IsDefault || snapshot.Layers.IsDefault)
        {
            throw new ArgumentException("Document collections are missing.");
        }
        var includedLayers = new HashSet<Guid>();
        foreach (var layer in snapshot.Layers)
        {
            if (layer is null)
            {
                throw new ArgumentException("Invalid document layer.");
            }
            if (includedLayerIds is null || includedLayerIds.Contains(layer.LayerId))
            {
                includedLayers.Add(layer.LayerId);
            }
        }

        double left = double.PositiveInfinity;
        double top = double.PositiveInfinity;
        double right = double.NegativeInfinity;
        double bottom = double.NegativeInfinity;
        foreach (var stroke in snapshot.Strokes)
        {
            if (stroke is null || stroke.Points.IsDefault)
            {
                throw new ArgumentException("Invalid document stroke.");
            }
            if (!includedLayers.Contains(stroke.LayerId))
            {
                continue;
            }
            ValidateWidth(stroke.Width);
            foreach (var point in stroke.Points)
            {
                Include(point.X + stroke.Offset.X, point.Y + stroke.Offset.Y, stroke.Width / 2);
            }
        }
        foreach (var shape in snapshot.Shapes)
        {
            if (shape is null)
            {
                throw new ArgumentException("Invalid document shape.");
            }
            if (!includedLayers.Contains(shape.LayerId))
            {
                continue;
            }
            ValidateWidth(shape.Width);
            if (!Enum.IsDefined(shape.Kind))
            {
                throw new ArgumentException("Unknown shape kind.");
            }
            Include(shape.Start.X + shape.Offset.X, shape.Start.Y + shape.Offset.Y, shape.Width / 2);
            Include(shape.End.X + shape.Offset.X, shape.End.Y + shape.Offset.Y, shape.Width / 2);
        }
        if (double.IsPositiveInfinity(left))
        {
            return new ExportBounds(0, 0, 1, 1);
        }
        double width = right - left;
        double height = bottom - top;
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0)
        {
            throw new ArgumentException("Document bounds are too large to export.");
        }
        return new ExportBounds(left, top, width, height);

        void Include(double x, double y, double radius)
        {
            if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(x - radius)
                || !double.IsFinite(y - radius) || !double.IsFinite(x + radius) || !double.IsFinite(y + radius))
            {
                throw new ArgumentException("Document coordinates are too large to export.");
            }
            left = Math.Min(left, x - radius);
            top = Math.Min(top, y - radius);
            right = Math.Max(right, x + radius);
            bottom = Math.Max(bottom, y + radius);
        }
    }

    private static XElement CreateStroke(Stroke stroke)
    {
        if (stroke.Points.Length == 1)
        {
            var point = stroke.Points[0];
            var circle = new XElement(Svg + "circle", new XAttribute("id", $"object-{stroke.StrokeId}"),
                new XAttribute("cx", Number(point.X + stroke.Offset.X)), new XAttribute("cy", Number(point.Y + stroke.Offset.Y)),
                new XAttribute("r", Number(stroke.Width / 2)));
            SetColor(circle, "fill", stroke.Color);
            return circle;
        }
        var path = new StringBuilder();
        for (int index = 0; index < stroke.Points.Length; index++)
        {
            var point = stroke.Points[index];
            path.Append(index == 0 ? "M " : " L ");
            path.Append(Number(point.X + stroke.Offset.X)).Append(' ').Append(Number(point.Y + stroke.Offset.Y));
        }
        var element = new XElement(Svg + "path", new XAttribute("id", $"object-{stroke.StrokeId}"), new XAttribute("d", path.ToString()),
            new XAttribute("fill", "none"), new XAttribute("stroke-width", Number(stroke.Width)),
            new XAttribute("stroke-linecap", "round"), new XAttribute("stroke-linejoin", "round"));
        SetColor(element, "stroke", stroke.Color);
        return element;
    }

    private static XElement CreateShape(Shape shape)
    {
        double startX = shape.Start.X + shape.Offset.X;
        double startY = shape.Start.Y + shape.Offset.Y;
        double endX = shape.End.X + shape.Offset.X;
        double endY = shape.End.Y + shape.Offset.Y;
        double width = Math.Abs(endX - startX);
        double height = Math.Abs(endY - startY);
        XElement element;
        switch (shape.Kind)
        {
            case ShapeKind.Line:
                element = new XElement(Svg + "line", new XAttribute("x1", Number(startX)), new XAttribute("y1", Number(startY)),
                    new XAttribute("x2", Number(endX)), new XAttribute("y2", Number(endY)));
                break;
            case ShapeKind.Rectangle:
                element = new XElement(Svg + "rect", new XAttribute("x", Number(Math.Min(startX, endX))), new XAttribute("y", Number(Math.Min(startY, endY))),
                    new XAttribute("width", Number(width)), new XAttribute("height", Number(height)));
                break;
            case ShapeKind.Ellipse:
                element = new XElement(Svg + "ellipse", new XAttribute("cx", Number(Math.Min(startX, endX) + width / 2)),
                    new XAttribute("cy", Number(Math.Min(startY, endY) + height / 2)), new XAttribute("rx", Number(width / 2)), new XAttribute("ry", Number(height / 2)));
                break;
            default:
                throw new ArgumentException("Unknown shape kind.");
        }
        element.SetAttributeValue("id", $"object-{shape.ShapeId}");
        element.SetAttributeValue("stroke-width", Number(shape.Width));
        SetColor(element, "stroke", shape.Color);
        if (shape.Fill is uint fill && shape.Kind != ShapeKind.Line)
        {
            SetColor(element, "fill", fill);
        }
        else
        {
            element.SetAttributeValue("fill", "none");
        }
        return element;
    }

    private static void SetColor(XElement element, string name, uint color)
    {
        element.SetAttributeValue(name, $"#{color & 0xFFFFFF:X6}");
        element.SetAttributeValue($"{name}-opacity", Number((color >> 24) / 255.0));
    }

    private static void ValidateWidth(double width)
    {
        if (!double.IsFinite(width) || width <= 0)
        {
            throw new ArgumentException("Line width must be a positive finite value.");
        }
    }

    private static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);
}
