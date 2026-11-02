using Avalonia.Controls;
using Avalonia.Headless;
using P2PDraw.App.Documents;
using P2PDraw.Core.Sync;
using Xunit;

namespace P2PDraw.Tests.App;

public partial class EditorTests
{
    [Fact]
    public async Task CancelledFileActionsDoNotOpenSystemDialogs()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(EditorTests));
        await session.Dispatch(async () =>
        {
            var window = new Window();
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var engine = new SyncEngine(Guid.NewGuid(), Guid.NewGuid());
            var snapshot = engine.CreateSnapshot();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AppFilesService.OpenProjectAsync(window.StorageProvider, cancellation.Token));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AppFilesService.SaveProjectAsync(window.StorageProvider, snapshot, cancellationToken: cancellation.Token));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AppFilesService.SaveSvgAsync(window.StorageProvider, snapshot, cancellationToken: cancellation.Token));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AppFilesService.SavePngAsync(window.StorageProvider, snapshot, cancellationToken: cancellation.Token));
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task HeadlessStorageReportsUnavailableSystemDialogs()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(EditorTests));
        await session.Dispatch(async () =>
        {
            var window = new Window();
            var engine = new SyncEngine(Guid.NewGuid(), Guid.NewGuid());
            var snapshot = engine.CreateSnapshot();
            Assert.False(window.StorageProvider.CanOpen);
            Assert.False(window.StorageProvider.CanSave);

            await Assert.ThrowsAsync<NotSupportedException>(() => AppFilesService.OpenProjectAsync(window.StorageProvider, TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<NotSupportedException>(() => AppFilesService.SaveProjectAsync(window.StorageProvider, snapshot, cancellationToken: TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<NotSupportedException>(() => AppFilesService.SaveSvgAsync(window.StorageProvider, snapshot, cancellationToken: TestContext.Current.CancellationToken));
            return true;
        }, TestContext.Current.CancellationToken);
    }
}
