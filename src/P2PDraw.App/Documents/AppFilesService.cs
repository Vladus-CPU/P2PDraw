using System.Text;
using Avalonia.Platform.Storage;
using P2PDraw.App.Export;
using P2PDraw.Core.Export;
using P2PDraw.Core.Storage;
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

    public static async Task<bool> SaveProjectAsync(IStorageProvider storage, Snapshot snapshot, string name = "P2PDraw", CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        byte[] contents = await Task.Run(() => Encoding.UTF8.GetBytes(ProjectFile.Create(name, snapshot).Serialize()), cancellationToken);
        return await SaveAsync(storage, "p2pdraw", "Проєкт P2PDraw", "application/json", contents, cancellationToken);
    }

    public static async Task<ProjectFile?> OpenProjectAsync(IStorageProvider storage, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storage);
        cancellationToken.ThrowIfCancellationRequested();
        if (!storage.CanOpen)
        {
            throw new NotSupportedException("На цьому пристрої недоступне системне відкриття файлів.");
        }
        var fileType = new FilePickerFileType("Проєкт P2PDraw") { Patterns = ["*.p2pdraw"], MimeTypes = ["application/json"] };
        var options = new FilePickerOpenOptions { Title = "Відкрити проєкт P2PDraw", AllowMultiple = false, FileTypeFilter = [fileType] };
        var files = await storage.OpenFilePickerAsync(options);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (files.Count == 0)
            {
                return null;
            }
            await using var stream = await files[0].OpenReadAsync();
            return await ProjectFile.ReadAsync(stream, cancellationToken);
        }
        finally
        {
            foreach (var file in files)
            {
                file.Dispose();
            }
        }
    }

    private static async Task<bool> SaveAsync(IStorageProvider storage, string extension, string description, string mimeType, byte[] contents, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(storage);
        cancellationToken.ThrowIfCancellationRequested();
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
