using System.Text;
using Avalonia.Platform.Storage;
using P2PDraw.App.Export;
using P2PDraw.Core.Export;
using P2PDraw.Core.Sync;

namespace P2PDraw.App.Documents;

public static class AppFilesService
{
    public static Task<bool> SaveSvgAsync(IStorageProvider storage, Snapshot snapshot, IReadOnlySet<Guid>? includedLayerIds = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string svg = SvgExporter.Export(snapshot, includedLayerIds);
        return SaveAsync(storage, "svg", "Векторний малюнок SVG", "image/svg+xml", Encoding.UTF8.GetBytes(svg), cancellationToken);
    }

    public static Task<bool> SavePngAsync(IStorageProvider storage, Snapshot snapshot, IReadOnlySet<Guid>? includedLayerIds = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var buffer = new MemoryStream();
        PngExporter.Write(snapshot, buffer, includedLayerIds);
        return SaveAsync(storage, "png", "Зображення PNG", "image/png", buffer.ToArray(), cancellationToken);
    }

    private static async Task<bool> SaveAsync(IStorageProvider storage, string extension, string description, string mimeType, byte[] contents, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(storage);
        if (!storage.CanSave)
        {
            throw new NotSupportedException("На цьому пристрої недоступне системне збереження файлів.");
        }
        var fileType = new FilePickerFileType(description) { Patterns = [$"*.{extension}"], MimeTypes = [mimeType] };
        var options = new FilePickerSaveOptions
        {
            Title = "Зберегти малюнок",
            SuggestedFileName = $"P2PDraw.{extension}",
            DefaultExtension = extension,
            FileTypeChoices = [fileType],
            ShowOverwritePrompt = true
        };
        using var file = await storage.SaveFilePickerAsync(options);
        cancellationToken.ThrowIfCancellationRequested();
        if (file is null)
        {
            return false;
        }
        await using var stream = await file.OpenWriteAsync();
        if (stream.CanSeek)
        {
            stream.SetLength(0);
        }
        await stream.WriteAsync(contents, cancellationToken);
        return true;
    }
}
