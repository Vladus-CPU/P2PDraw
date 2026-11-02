using Avalonia;
using Avalonia.Headless;
using P2PDraw.App.Export;
using P2PDraw.Core.Models;
using P2PDraw.Core.Sync;
using SkiaSharp;
using Xunit;

namespace P2PDraw.Tests.App;

[Collection("Avalonia")]
public class PngExporterTests
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<Application>().UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });

    [Fact]
    public async Task ExportProducesRealPngWithTranslatedGeometryAndWhiteBackground()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(PngExporterTests));
        await session.Dispatch(() =>
        {
            var engine = new SyncEngine(Guid.NewGuid(), Guid.NewGuid());
            var rectangle = engine.AddShape(ShapeKind.Rectangle, new(-20, -30), new(0, -10), 0xFFFF0000, 2, 0xFFFF0000);
            engine.MoveObject(rectangle.ShapeId, new(-10, -20));
            engine.AddShape(ShapeKind.Line, new(0, 0), new(10, 0), 0xFF000000, 2);
            using var contents = new MemoryStream();

            PngExporter.Write(engine.CreateSnapshot(), contents);

            byte[] bytes = contents.ToArray();
            Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, bytes[..8]);
            using var bitmap = SKBitmap.Decode(bytes);
            Assert.Equal(42, bitmap.Width);
            Assert.Equal(52, bitmap.Height);
            Assert.Equal(SKColors.Red, bitmap.GetPixel(10, 10));
            Assert.Equal(SKColors.White, bitmap.GetPixel(35, 10));
            Assert.Equal(SKColors.Black, bitmap.GetPixel(35, 51));
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ExportUsesLayerOrderAndStrokesBeforeShapes()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(PngExporterTests));
        await session.Dispatch(() =>
        {
            var engine = new SyncEngine(Guid.NewGuid(), Guid.NewGuid());
            var upper = engine.AddLayer("Верхній", 10);
            engine.AddShape(ShapeKind.Rectangle, new(0, 0), new(20, 20), 0xFF0000FF, 2, 0xFF0000FF);
            engine.AddShape(ShapeKind.Rectangle, new(0, 0), new(20, 20), 0xFFFF0000, 2, 0xFFFF0000, upper.LayerId);
            var stroke = engine.BeginStroke(0xFF00FF00, 10, upper.LayerId);
            engine.AppendStroke(stroke.StrokeId, [new(5, 10), new(15, 10)]);
            engine.EndStroke(stroke.StrokeId);
            using var contents = new MemoryStream();

            PngExporter.Write(engine.CreateSnapshot(), contents);

            using var bitmap = SKBitmap.Decode(contents.ToArray());
            Assert.Equal(SKColors.Red, bitmap.GetPixel(10, 10));
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ExportOnlyIncludedLayers()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(PngExporterTests));
        await session.Dispatch(() =>
        {
            var engine = new SyncEngine(Guid.NewGuid(), Guid.NewGuid());
            var hidden = engine.AddLayer("Прихований");
            engine.AddShape(ShapeKind.Rectangle, new(0, 0), new(20, 20), 0xFF0000FF, 2, 0xFF0000FF);
            engine.AddShape(ShapeKind.Rectangle, new(0, 0), new(5000, 5000), 0xFFFF0000, 2, 0xFFFF0000, hidden.LayerId);
            using var contents = new MemoryStream();

            PngExporter.Write(engine.CreateSnapshot(), contents, new HashSet<Guid> { Layer.DefaultLayerId });

            using var bitmap = SKBitmap.Decode(contents.ToArray());
            Assert.Equal(22, bitmap.Width);
            Assert.Equal(22, bitmap.Height);
            Assert.Equal(SKColors.Blue, bitmap.GetPixel(10, 10));
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ExportRendersPointStrokeAndEllipseFill()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(PngExporterTests));
        await session.Dispatch(() =>
        {
            var engine = new SyncEngine(Guid.NewGuid(), Guid.NewGuid());
            var dot = engine.BeginStroke(0xFF00FF00, 6);
            engine.AppendStroke(dot.StrokeId, [new(0, 0)]);
            engine.EndStroke(dot.StrokeId);
            engine.AddShape(ShapeKind.Ellipse, new(10, -3), new(16, 3), 0xFFFF0000, 2, 0xFFFF0000);
            using var contents = new MemoryStream();

            PngExporter.Write(engine.CreateSnapshot(), contents);

            using var bitmap = SKBitmap.Decode(contents.ToArray());
            Assert.Equal(SKColors.Lime, bitmap.GetPixel(3, 4));
            Assert.Equal(SKColors.Red, bitmap.GetPixel(16, 4));
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task EmptyDocumentExportsSingleWhitePixel()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(PngExporterTests));
        await session.Dispatch(() =>
        {
            var engine = new SyncEngine(Guid.NewGuid(), Guid.NewGuid());
            using var contents = new MemoryStream();

            PngExporter.Write(engine.CreateSnapshot(), contents);

            using var bitmap = SKBitmap.Decode(contents.ToArray());
            Assert.Equal(1, bitmap.Width);
            Assert.Equal(1, bitmap.Height);
            Assert.Equal(SKColors.White, bitmap.GetPixel(0, 0));
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(5000, 10)]
    [InlineData(4000, 4000)]
    public async Task OversizedDocumentIsRejectedBeforeWriting(double width, double height)
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(PngExporterTests));
        await session.Dispatch(() =>
        {
            var engine = new SyncEngine(Guid.NewGuid(), Guid.NewGuid());
            engine.AddShape(ShapeKind.Rectangle, new(0, 0), new(width, height), 0xFF000000, 2);
            using var contents = new MemoryStream();

            var error = Assert.Throws<ArgumentException>(() => PngExporter.Write(engine.CreateSnapshot(), contents));

            Assert.Contains("SVG", error.Message);
            Assert.Equal(0, contents.Length);
            return true;
        }, TestContext.Current.CancellationToken);
    }
}
