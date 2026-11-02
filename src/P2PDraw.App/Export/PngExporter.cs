using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using P2PDraw.Core.Export;
using P2PDraw.Core.Sync;

namespace P2PDraw.App.Export;

public static class PngExporter
{
    public const int MaximumEdge = 4096;
    public const int MaximumPixels = 16_000_000;

    public static void Write(Snapshot snapshot, Stream destination, IReadOnlySet<Guid>? includedLayerIds = null)
    {
        ArgumentNullException.ThrowIfNull(destination);
        Dispatcher.UIThread.VerifyAccess();
        var bounds = SvgExporter.GetBounds(snapshot, includedLayerIds);
        double width = Math.Ceiling(bounds.Width);
        double height = Math.Ceiling(bounds.Height);
        if (width > MaximumEdge || height > MaximumEdge || width * height > MaximumPixels)
        {
            throw new ArgumentException("PNG завеликий. Максимум — 4096 пікселів на сторону та 16 мільйонів пікселів загалом. Для більшого документа виберіть SVG.");
        }
        using var bitmap = new RenderTargetBitmap(new PixelSize((int)width, (int)height), new Vector(96, 96));
        using (var context = bitmap.CreateDrawingContext())
        {
            context.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
            using var transform = context.PushTransform(Matrix.CreateTranslation(-bounds.X, -bounds.Y));
            foreach (var layer in snapshot.Layers)
            {
                if (includedLayerIds is not null && !includedLayerIds.Contains(layer.LayerId))
                {
                    continue;
                }
                foreach (var stroke in snapshot.Strokes)
                {
                    if (stroke.LayerId == layer.LayerId)
                    {
                        DocumentRenderer.DrawStroke(context, stroke, stroke.Offset);
                    }
                }
                foreach (var shape in snapshot.Shapes)
                {
                    if (shape.LayerId == layer.LayerId)
                    {
                        DocumentRenderer.DrawShape(context, shape);
                    }
                }
            }
        }
        bitmap.Save(destination, new PngBitmapEncoderOptions());
    }
}
