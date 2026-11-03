using Avalonia.Platform.Storage;
using P2PDraw.App.ViewModels;
using P2PDraw.Core.Storage;
using P2PDraw.Core.Sync;

namespace P2PDraw.App.Documents;

public sealed class EditorFileActions
{
    private readonly MainWindowViewModel model;

    public EditorFileActions(MainWindowViewModel model)
    {
        this.model = model;
    }

    public async Task ExportAsync(IStorageProvider storage, bool png, Snapshot snapshot, IReadOnlySet<Guid>? includedLayerIds)
    {
        try
        {
            bool saved = png
                ? await AppFilesService.SavePngAsync(storage, snapshot, includedLayerIds)
                : await AppFilesService.SaveSvgAsync(storage, snapshot, includedLayerIds);
            if (saved)
            {
                model.Session.Status = png ? "PNG збережено." : "SVG збережено.";
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (NotSupportedException)
        {
            model.Session.Status = "Системне збереження файлів недоступне.";
        }
        catch (ArgumentException)
        {
            model.Session.Status = png
                ? "PNG підтримує до 4096 px на сторону та 16 Мп загалом. Для більшого документа виберіть SVG."
                : "Не вдалося визначити межі документа. Перевірте координати об'єктів.";
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException || exception is InvalidOperationException)
        {
            model.Session.Status = "Не вдалося зберегти файл. Виберіть інше місце збереження.";
        }
    }

    public async Task SaveProjectAsync(IStorageProvider storage)
    {
        try
        {
            if (await AppFilesService.SaveProjectAsync(storage, model.GetSnapshot()))
            {
                model.Session.Status = "Проєкт P2PDraw збережено.";
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (InvalidDataException exception)
        {
            model.Session.Status = exception.Message;
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException
            || exception is ArgumentException || exception is NotSupportedException || exception is InvalidOperationException)
        {
            model.Session.Status = "Не вдалося зберегти проєкт. Перевірте доступ до файлу.";
        }
    }

    public async Task<ProjectFile?> ChooseProjectAsync(IStorageProvider storage)
    {
        try
        {
            return await AppFilesService.OpenProjectAsync(storage);
        }
        catch (OperationCanceledException)
        {
        }
        catch (InvalidDataException exception)
        {
            model.Session.Status = exception.Message;
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException
            || exception is ArgumentException || exception is NotSupportedException || exception is InvalidOperationException)
        {
            model.Session.Status = "Не вдалося відкрити проєкт. Виберіть коректний файл .p2pdraw.";
        }
        return null;
    }

    public async Task<bool> OpenProjectAsync(ProjectFile file)
    {
        try
        {
            if (await model.OpenAsNewSessionAsync(file.Snapshot))
            {
                return true;
            }
            model.Session.Status = "Завершіть поточне підключення та спробуйте відкрити файл ще раз.";
        }
        catch (OperationCanceledException)
        {
        }
        catch (ArgumentException exception)
        {
            model.Session.Status = exception.Message;
        }
        return false;
    }
}
