using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;
using P2PDraw.Core.Export;
using P2PDraw.Core.Models;
using P2PDraw.Core.Sync;
using Xunit;

namespace P2PDraw.Tests.Core;

public class SvgExporterTests
{
    private static readonly XNamespace Svg = "http://www.w3.org/2000/svg";

    [Fact]
    public void ExportPreservesStrokePointsOffsetAndTransparency()
    {
        var engine = new SyncEngine(Guid.NewGuid(), Guid.NewGuid());
        var stroke = engine.BeginStroke(0x80402010, 4);
        engine.AppendStroke(stroke.StrokeId, [new(-10, -20), new(20, 10)]);
        engine.EndStroke(stroke.StrokeId);
        engine.MoveObject(stroke.StrokeId, new(-5, 3));

        var document = XElement.Parse(SvgExporter.Export(engine.CreateSnapshot()));

        Assert.Equal(Svg + "svg", document.Name);
        Assert.Equal("-17 -19 34 34", document.Attribute("viewBox")!.Value);
        var path = Assert.Single(document.Descendants(Svg + "path"));
        Assert.Equal("M -15 -17 L 15 13", path.Attribute("d")!.Value);
        Assert.Equal("#402010", path.Attribute("stroke")!.Value);
        Assert.Equal(128 / 255.0, double.Parse(path.Attribute("stroke-opacity")!.Value, CultureInfo.InvariantCulture));
        Assert.Equal("4", path.Attribute("stroke-width")!.Value);
        Assert.Equal("round", path.Attribute("stroke-linecap")!.Value);
        Assert.Equal("none", path.Attribute("fill")!.Value);
    }

    [Fact]
    public void ExportPreservesAllShapeGeometryAndFill()
    {
        var engine = new SyncEngine(Guid.NewGuid(), Guid.NewGuid());
        var line = engine.AddShape(ShapeKind.Line, new(1, 2), new(3, 4), 0xFF112233, 2);
        engine.MoveObject(line.ShapeId, new(10, -10));
        engine.AddShape(ShapeKind.Rectangle, new(30, 40), new(10, 20), 0xFF445566, 3, 0x4000FF00);
        engine.AddShape(ShapeKind.Ellipse, new(-20, -10), new(20, 10), 0xFF778899, 1);

        var document = XElement.Parse(SvgExporter.Export(engine.CreateSnapshot()));

        var lineElement = Assert.Single(document.Descendants(Svg + "line"));
        Assert.Equal("11", lineElement.Attribute("x1")!.Value);
        Assert.Equal("-8", lineElement.Attribute("y1")!.Value);
        Assert.Equal("13", lineElement.Attribute("x2")!.Value);
        Assert.Equal("-6", lineElement.Attribute("y2")!.Value);
        var rectangle = Assert.Single(document.Descendants(Svg + "rect"));
        Assert.Equal("10", rectangle.Attribute("x")!.Value);
        Assert.Equal("20", rectangle.Attribute("y")!.Value);
        Assert.Equal("20", rectangle.Attribute("width")!.Value);
        Assert.Equal("20", rectangle.Attribute("height")!.Value);
        Assert.Equal("#00FF00", rectangle.Attribute("fill")!.Value);
        Assert.Equal(64 / 255.0, double.Parse(rectangle.Attribute("fill-opacity")!.Value, CultureInfo.InvariantCulture));
        var ellipse = Assert.Single(document.Descendants(Svg + "ellipse"));
        Assert.Equal("0", ellipse.Attribute("cx")!.Value);
        Assert.Equal("0", ellipse.Attribute("cy")!.Value);
        Assert.Equal("20", ellipse.Attribute("rx")!.Value);
        Assert.Equal("10", ellipse.Attribute("ry")!.Value);
        Assert.Equal("none", ellipse.Attribute("fill")!.Value);
    }

    [Fact]
    public void ExportDrawsLayersInCanvasOrderWithStrokesBeforeShapes()
    {
        var engine = new SyncEngine(Guid.NewGuid(), Guid.NewGuid());
        var upper = engine.AddLayer("Верхній & <видимий>", 10);
        var lower = engine.AddLayer("Нижній", -10);
        var shape = engine.AddShape(ShapeKind.Rectangle, new(0, 0), new(10, 10), 0xFF000000, 1, layerId: lower.LayerId);
        var stroke = engine.BeginStroke(0xFF000000, 1, lower.LayerId);
        engine.AppendStroke(stroke.StrokeId, [new(0, 0), new(10, 10)]);
        engine.EndStroke(stroke.StrokeId);
        engine.AddShape(ShapeKind.Ellipse, new(0, 0), new(10, 10), 0xFF000000, 1, layerId: upper.LayerId);

        string svg = SvgExporter.Export(engine.CreateSnapshot());
        var groups = XElement.Parse(svg).Elements(Svg + "g").ToArray();

        Assert.Equal($"layer-{lower.LayerId}", groups[0].Attribute("id")!.Value);
        Assert.Equal($"layer-{Layer.DefaultLayerId}", groups[1].Attribute("id")!.Value);
        Assert.Equal($"layer-{upper.LayerId}", groups[2].Attribute("id")!.Value);
        var objects = groups[0].Elements().ToArray();
        Assert.Equal($"object-{stroke.StrokeId}", objects[0].Attribute("id")!.Value);
        Assert.Equal($"object-{shape.ShapeId}", objects[1].Attribute("id")!.Value);
        Assert.Equal("Верхній & <видимий>", groups[2].Attribute("data-name")!.Value);
        Assert.Contains("&amp; &lt;видимий&gt;", svg);
    }

    [Fact]
    public void ExportOnlyIncludedLayersAndTheirBounds()
    {
        var engine = new SyncEngine(Guid.NewGuid(), Guid.NewGuid());
        var hidden = engine.AddLayer("Прихований");
        engine.AddShape(ShapeKind.Rectangle, new(0, 0), new(10, 10), 0xFF000000, 2);
        engine.AddShape(ShapeKind.Rectangle, new(1000, 1000), new(2000, 2000), 0xFF000000, 10, layerId: hidden.LayerId);
        var included = new HashSet<Guid> { Layer.DefaultLayerId };

        var document = XElement.Parse(SvgExporter.Export(engine.CreateSnapshot(), included));

        Assert.Single(document.Elements(Svg + "g"));
        Assert.Single(document.Descendants(Svg + "rect"));
        Assert.Equal("-1 -1 12 12", document.Attribute("viewBox")!.Value);
    }

    [Fact]
    public void ExportSinglePointStrokeAsCircle()
    {
        var engine = new SyncEngine(Guid.NewGuid(), Guid.NewGuid());
        var stroke = engine.BeginStroke(0xFF010203, 6);
        engine.AppendStroke(stroke.StrokeId, [new(4, 8)]);
        engine.EndStroke(stroke.StrokeId);

        var document = XElement.Parse(SvgExporter.Export(engine.CreateSnapshot()));

        var dot = Assert.Single(document.Descendants(Svg + "circle"));
        Assert.Equal("4", dot.Attribute("cx")!.Value);
        Assert.Equal("8", dot.Attribute("cy")!.Value);
        Assert.Equal("3", dot.Attribute("r")!.Value);
        Assert.Equal("1 5 6 6", document.Attribute("viewBox")!.Value);
    }

    [Fact]
    public void ExportUsesInvariantNumbers()
    {
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("uk-UA");
            var engine = new SyncEngine(Guid.NewGuid(), Guid.NewGuid());
            engine.AddShape(ShapeKind.Line, new(0.5, 1.25), new(10.75, 20.5), 0xFF000000, 1.5);

            var document = XElement.Parse(SvgExporter.Export(engine.CreateSnapshot()));

            var line = Assert.Single(document.Descendants(Svg + "line"));
            Assert.Equal("0.5", line.Attribute("x1")!.Value);
            Assert.Equal("1.25", line.Attribute("y1")!.Value);
            Assert.Equal("1.5", line.Attribute("stroke-width")!.Value);
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    [Fact]
    public void EmptyAndFullyHiddenDocumentsHaveUnitBounds()
    {
        var engine = new SyncEngine(Guid.NewGuid(), Guid.NewGuid());
        Assert.Equal(new ExportBounds(0, 0, 1, 1), SvgExporter.GetBounds(engine.CreateSnapshot()));
        engine.AddShape(ShapeKind.Rectangle, new(0, 0), new(10, 10), 0xFF000000, 1);

        string svg = SvgExporter.Export(engine.CreateSnapshot(), new HashSet<Guid>());

        var document = XElement.Parse(svg);
        Assert.Empty(document.Elements());
        Assert.Equal("0 0 1 1", document.Attribute("viewBox")!.Value);
    }

    [Fact]
    public void ExportIgnoresDeletedObjectsAndDeletedLayers()
    {
        var engine = new SyncEngine(Guid.NewGuid(), Guid.NewGuid());
        var deletedShape = engine.AddShape(ShapeKind.Rectangle, new(0, 0), new(10, 10), 0xFF000000, 1);
        engine.RemoveObject(deletedShape.ShapeId);
        var layer = engine.AddLayer("Видалений");
        engine.AddShape(ShapeKind.Ellipse, new(0, 0), new(10, 10), 0xFF000000, 1, layerId: layer.LayerId);
        engine.RemoveLayer(layer.LayerId);

        var document = XElement.Parse(SvgExporter.Export(engine.CreateSnapshot()));

        Assert.Single(document.Elements(Svg + "g"));
        Assert.Empty(document.Descendants(Svg + "rect"));
        Assert.Empty(document.Descendants(Svg + "ellipse"));
        Assert.Equal("0 0 1 1", document.Attribute("viewBox")!.Value);
    }

    [Fact]
    public void ExportLargeFiniteDocumentWithoutRasterLimit()
    {
        var engine = new SyncEngine(Guid.NewGuid(), Guid.NewGuid());
        engine.AddShape(ShapeKind.Line, new(-10000, -20000), new(10000, 20000), 0xFF000000, 2);

        var document = XElement.Parse(SvgExporter.Export(engine.CreateSnapshot()));

        Assert.Equal("-10001 -20001 20002 40002", document.Attribute("viewBox")!.Value);
    }

    [Fact]
    public void ExportRejectsCoordinateOverflow()
    {
        var engine = new SyncEngine(Guid.NewGuid(), Guid.NewGuid());
        engine.AddShape(ShapeKind.Line, new(-double.MaxValue, 0), new(double.MaxValue, 1), 0xFF000000, 1);

        Assert.Throws<ArgumentException>(() => SvgExporter.Export(engine.CreateSnapshot()));
    }

    [Fact]
    public void ExportDoesNotChangeDocumentLogOrClock()
    {
        var engine = new SyncEngine(Guid.NewGuid(), Guid.NewGuid());
        engine.AddShape(ShapeKind.Rectangle, new(0, 0), new(10, 10), 0xFF000000, 1);
        var before = engine.CreateSnapshot();

        SvgExporter.Export(before);

        Assert.Equal(JsonSerializer.Serialize(before), JsonSerializer.Serialize(engine.CreateSnapshot()));
    }
}
