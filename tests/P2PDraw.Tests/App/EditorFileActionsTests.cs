using Avalonia.Controls;
using Avalonia.Headless;
using P2PDraw.App.Documents;
using P2PDraw.App.ViewModels;
using Xunit;

namespace P2PDraw.Tests.App;

public partial class EditorTests
{
    [Theory]
    [InlineData("svg")]
    [InlineData("save")]
    [InlineData("open")]
    public async Task UnavailableFileDialogsReportFailureWithoutReplacingDocument(string action)
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(EditorTests));
        await session.Dispatch(async () =>
        {
            await using var model = new MainWindowViewModel();
            model.BeginStroke(new(10, 20));
            model.EndStroke();
            string sessionId = model.SessionId;
            int operationCount = model.OperationCount;
            var storage = new Window().StorageProvider;
            var files = new EditorFileActions(model);
            Assert.False(storage.CanSave);
            Assert.False(storage.CanOpen);

            if (action == "svg")
            {
                await files.ExportAsync(storage, false, model.GetSnapshot(), null);
                Assert.Equal("Системне збереження файлів недоступне.", model.Status);
            }
            else if (action == "save")
            {
                await files.SaveProjectAsync(storage);
                Assert.Equal("Не вдалося зберегти проєкт. Перевірте доступ до файлу.", model.Status);
            }
            else
            {
                Assert.Null(await files.ChooseProjectAsync(storage));
                Assert.Equal("Не вдалося відкрити проєкт. Виберіть коректний файл .p2pdraw.", model.Status);
            }

            Assert.Equal(sessionId, model.SessionId);
            Assert.Equal(operationCount, model.OperationCount);
            Assert.Single(model.Strokes);
            Assert.True(model.CanUndo);
            return true;
        }, TestContext.Current.CancellationToken);
    }
}
