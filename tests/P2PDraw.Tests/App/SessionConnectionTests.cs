using System.Net.Sockets;
using Avalonia.Headless;
using Avalonia.Threading;
using P2PDraw.App.ViewModels;
using P2PDraw.Networking.Discovery;
using P2PDraw.Networking.Protocol;
using P2PDraw.Networking.Transport;
using Xunit;

namespace P2PDraw.Tests.App;

public partial class EditorTests
{
    [Fact]
    public async Task ConnectionStatusRecoversAndIgnoresRejectedExtraClient()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(EditorTests));
        await session.Dispatch(async () =>
        {
            string sessionId = Guid.NewGuid().ToString();
            await using var first = new MainWindowViewModel(["0", sessionId, "1"]);
            await using var second = new MainWindowViewModel(["0", sessionId, "1"]);
            await first.StartListeningAsync();
            await second.StartListeningAsync();
            second.RemotePort = first.LocalPort;
            await second.ConnectAsync();
            await WaitUntilAsync(() => first.ConnectedPeers == 1 && second.ConnectedPeers == 1);

            using var rejected = new TcpClient();
            await rejected.ConnectAsync("127.0.0.1", int.Parse(first.LocalPort), TestContext.Current.CancellationToken);
            var stream = rejected.GetStream();
            var wrongSession = new HelloMessage(Guid.NewGuid(), Guid.NewGuid(), TcpPeer.ProtocolVersion);
            await MessageFraming.WriteAsync<NetworkMessage>(stream, wrongSession, TestContext.Current.CancellationToken);
            var buffer = new byte[1];
            Assert.Equal(0, await stream.ReadAsync(buffer, TestContext.Current.CancellationToken));
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            Assert.Equal(1, first.ConnectedPeers);
            Assert.Contains("З'єднання встановлено", first.Status);

            await second.DisposeAsync();
            await WaitUntilAsync(() => first.ConnectedPeers == 0);
            Assert.Contains("З'єднання втрачено", first.Status);
            await using var returning = new MainWindowViewModel(["0", sessionId, "1"]);
            await returning.StartListeningAsync();
            returning.RemotePort = first.LocalPort;
            await returning.ConnectAsync();
            await WaitUntilAsync(() => first.ConnectedPeers == 1 && returning.ConnectedPeers == 1);
            Assert.Contains("З'єднання встановлено", first.Status);
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ManualSessionMismatchCanBeCorrectedWithoutRestart()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(EditorTests));
        await session.Dispatch(async () =>
        {
            await using var creator = new MainWindowViewModel();
            await using var joining = new MainWindowViewModel();
            await creator.CreateSessionAsync();
            joining.RemotePort = creator.LocalPort;
            await joining.JoinManualSessionAsync();
            Assert.Equal(0, joining.ConnectedPeers);
            Assert.Empty(joining.Strokes);
            Assert.True(joining.CanEditSession);
            Assert.Contains("Не вдалося підключитися", joining.Status);

            joining.SessionId = creator.SessionId;
            await joining.JoinManualSessionAsync();
            await WaitUntilAsync(() => joining.ConnectedPeers == 1 && creator.ConnectedPeers == 1);
            Assert.Equal(creator.SessionId, joining.GetSnapshot().SessionId.ToString());
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task CreateAndJoinSessionWithoutCopyingIdOrStartingListener()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(EditorTests));
        await session.Dispatch(async () =>
        {
            await using var creator = new MainWindowViewModel();
            await using var joining = new MainWindowViewModel();
            await creator.CreateSessionAsync();
            Assert.True(creator.CanConnect);
            Assert.True(int.Parse(creator.LocalPort) > 0);
            Assert.False(creator.CanCreateSession);
            Assert.NotEqual(creator.SessionId, joining.SessionId);

            await joining.SearchSessionsAsync();
            Assert.True(joining.CanStartListening);
            await WaitUntilAsync(() => joining.AvailableSessions.Any(found => found.SessionId.ToString() == creator.SessionId));
            joining.SelectedSession = joining.AvailableSessions.First(found => found.SessionId.ToString() == creator.SessionId);
            Assert.True(joining.CanJoinSession);
            await joining.JoinSelectedSessionAsync();
            await WaitUntilAsync(() => creator.ConnectedPeers == 1 && joining.ConnectedPeers == 1);
            Assert.Equal(creator.SessionId, joining.SessionId);
            joining.BeginStroke(new(10, 20));
            joining.EndStroke();
            await WaitUntilAsync(() => creator.OperationCount == 3);
            Assert.Equal(Normalize(creator), Normalize(joining));
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task JoiningAnotherSessionDoesNotDiscardExistingDrawing()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(EditorTests));
        await session.Dispatch(async () =>
        {
            await using var model = new MainWindowViewModel();
            model.BeginStroke(new(1, 2));
            model.EndStroke();
            string originalSession = model.SessionId;
            string originalState = Normalize(model);
            model.SelectedSession = new DiscoveredPeer(Guid.NewGuid(), "127.0.0.1", 5000, DateTimeOffset.UtcNow, Guid.NewGuid(), "Інша сесія");

            await model.JoinSelectedSessionAsync();

            Assert.Equal(originalSession, model.SessionId);
            Assert.Equal(originalState, Normalize(model));
            Assert.Contains("Документ уже містить зміни", model.Status);
            Assert.True(model.CanStartListening);
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public async Task SessionJoinBuildsFullMeshAndSurvivesCreatorExit(int count)
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(EditorTests));
        await session.Dispatch(async () =>
        {
            var models = new List<MainWindowViewModel>();
            try
            {
                var creator = new MainWindowViewModel();
                models.Add(creator);
                await creator.CreateSessionAsync();
                for (int index = 1; index < count; index++)
                {
                    var joining = new MainWindowViewModel();
                    models.Add(joining);
                    joining.SessionId = creator.SessionId;
                    joining.RemotePort = creator.LocalPort;
                    await joining.JoinManualSessionAsync();
                }
                await WaitUntilAsync(() => models.All(model => model.ConnectedPeers == count - 1));
                foreach (var model in models)
                {
                    model.BeginStroke(new(10, 20));
                    model.EndStroke();
                }
                await WaitUntilAsync(() => models.All(model => model.OperationCount == count * 3));
                foreach (var model in models)
                {
                    Assert.Equal(Normalize(creator), Normalize(model));
                }

                await creator.DisposeAsync();
                models.RemoveAt(0);
                await WaitUntilAsync(() => models.All(model => model.ConnectedPeers == count - 2));
                models[0].ClearCanvas();
                await WaitUntilAsync(() => models.All(model => model.OperationCount == count * 3 + 1));
                foreach (var model in models)
                {
                    Assert.Empty(model.Strokes);
                    Assert.Equal(count, model.GetSnapshot().Tombstones.Length);
                }
            }
            finally
            {
                foreach (var model in models)
                {
                    await model.DisposeAsync();
                }
            }
            return true;
        }, TestContext.Current.CancellationToken);
    }
}
