using System.Collections.Immutable;
using System.ComponentModel;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Avalonia.Threading;
using P2PDraw.Core.Operations;
using P2PDraw.Core.Sync;
using P2PDraw.Networking.Discovery;
using P2PDraw.Networking.Transport;

namespace P2PDraw.App.ViewModels;

public sealed class SessionViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private readonly Channel<Operation> outgoing = Channel.CreateUnbounded<Operation>();
    private readonly Task sendingTask;
    private TcpPeer? peer;
    private PeerDiscovery? discovery;
    private DiscoveredPeer? selectedPeer;
    private DiscoveredPeer? selectedSession;
    private bool hadConnection;
    private Task meshTask = Task.CompletedTask;
    private string discoveryStatus = "Пошук почнеться після запуску прослуховування.";
    private string sessionId = Guid.NewGuid().ToString();
    private string localPort = "5101";
    private string remoteAddress = "127.0.0.1";
    private string remotePort = "5102";
    private string status = "Можна малювати офлайн або підключити інший вузол.";
    private bool isSessionLocked;
    private bool isListening;
    private bool isBusy;
    private bool isOffline;
    private bool isDisposed;

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? DocumentChanged;
    public SyncEngine Engine { get; private set; }
    public int ConnectedPeers { get; private set; }
    public ImmutableArray<DiscoveredPeer> DiscoveredPeers { get; private set; } = [];
    public ImmutableArray<DiscoveredPeer> AvailableSessions { get; private set; } = [];
    public bool CanCreateSession => CanStartListening;
    public bool CanSearchSessions => !isBusy && !isDisposed;
    public bool CanJoinSession => SelectedSession is not null && !isBusy && !isDisposed;
    public string SessionTitle => isSessionLocked ? $"Сесія {Engine.SessionId.ToString()[..8]}" : "Локальний документ";

    public DiscoveredPeer? SelectedSession
    {
        get => selectedSession;
        set
        {
            selectedSession = value;
            Notify();
            Notify(nameof(CanJoinSession));
        }
    }
    public bool CanConnectDiscovered => CanConnect && SelectedPeer is not null;
    public string DiscoveryStatus
    {
        get => discoveryStatus;
        private set
        {
            discoveryStatus = value;
            Notify();
        }
    }

    public DiscoveredPeer? SelectedPeer
    {
        get => selectedPeer;
        set
        {
            selectedPeer = value;
            Notify();
            Notify(nameof(CanConnectDiscovered));
        }
    }
    public bool CanEditSession => !isSessionLocked || (Engine.GetOperations().IsEmpty && ConnectedPeers == 0);
    public bool CanStartListening => !isListening && !isBusy && !isDisposed;
    public bool CanConnect => isListening && !isBusy && !isDisposed;
    public bool CanDisconnect => isListening && !isBusy && !isDisposed;
    public string ConnectionStatus
    {
        get
        {
            if (ConnectedPeers > 0)
            {
                return "Підключено";
            }
            if (isOffline)
            {
                return "Офлайн";
            }
            if (hadConnection && isListening)
            {
                return "Повторне підключення…";
            }
            return isListening ? "Очікування учасників" : "Не підключено";
        }
    }

    public string SessionId
    {
        get => sessionId;
        set
        {
            sessionId = value;
            Notify();
        }
    }

    public string LocalPort
    {
        get => localPort;
        set
        {
            localPort = value;
            Notify();
        }
    }

    public string RemoteAddress
    {
        get => remoteAddress;
        set
        {
            remoteAddress = value;
            Notify();
        }
    }

    public string RemotePort
    {
        get => remotePort;
        set
        {
            remotePort = value;
            Notify();
        }
    }

    public string Status
    {
        get => status;
        set
        {
            status = value;
            Notify();
        }
    }

    public SessionViewModel(string[]? args = null)
    {
        if (args is not null && args.Length == 3)
        {
            localPort = args[0];
            sessionId = args[1];
            remotePort = args[2];
        }
        else if (args is not null && args.Length > 0)
        {
            status = "Параметри запуску: <локальний порт> <SessionId> <віддалений порт>.";
        }
        bool hasValidSession = Guid.TryParse(sessionId, out var parsedSession) && parsedSession != Guid.Empty;
        Guid initialSession = hasValidSession ? parsedSession : Guid.NewGuid();
        Engine = new SyncEngine(Guid.NewGuid(), initialSession);
        sendingTask = Task.Run(SendOperationsAsync);
    }

    public async Task CreateSessionAsync()
    {
        if (!CanCreateSession)
        {
            return;
        }
        if (Engine.GetOperations().IsEmpty && !isSessionLocked)
        {
            SessionId = Guid.NewGuid().ToString();
        }
        LocalPort = "0";
        await StartListeningAsync();
        if (isListening)
        {
            Status = "Сесію створено. Інші пристрої можуть приєднатися.";
        }
    }

    public async Task SearchSessionsAsync()
    {
        if (!CanSearchSessions)
        {
            return;
        }
        SetBusy(true);
        DiscoveryStatus = "Пошук сесій…";
        try
        {
            await StartDiscoveryAsync(includeOtherSessions: true);
        }
        finally
        {
            SetBusy(false);
        }
    }

    public async Task JoinSelectedSessionAsync()
    {
        var selected = SelectedSession;
        if (!CanJoinSession || selected is null)
        {
            return;
        }
        if (!await PrepareSessionAsync(selected.SessionId))
        {
            return;
        }
        RemoteAddress = selected.Address;
        RemotePort = selected.TcpPort.ToString();
        if (!isListening)
        {
            LocalPort = "0";
            await StartListeningAsync();
        }
        await ConnectAsync();
    }

    public async Task JoinManualSessionAsync()
    {
        if (isBusy || isDisposed)
        {
            return;
        }
        if (!Guid.TryParse(SessionId, out var requestedSession) || requestedSession == Guid.Empty)
        {
            Status = "Вкажіть повний ідентифікатор сесії з іншого пристрою.";
            return;
        }
        if (!IPAddress.TryParse(RemoteAddress, out _) || !int.TryParse(RemotePort, out int port) || port < 1 || port > 65535)
        {
            Status = "Перевірте IP-адресу та порт іншого пристрою.";
            return;
        }
        if (!await PrepareSessionAsync(requestedSession))
        {
            return;
        }
        if (!isListening)
        {
            LocalPort = "0";
            await StartListeningAsync();
        }
        await ConnectAsync();
    }

    private async Task<bool> PrepareSessionAsync(Guid requestedSession)
    {
        if (requestedSession == Guid.Empty || isDisposed || isBusy)
        {
            return false;
        }
        if (Engine.SessionId == requestedSession)
        {
            return true;
        }
        if (!Engine.GetOperations().IsEmpty)
        {
            Status = "Документ уже містить зміни. Іншу сесію не відкрито, щоб не втратити малюнок.";
            return false;
        }
        SetBusy(true);
        try
        {
            if (discovery is not null)
            {
                discovery.Changed -= OnDiscoveryChanged;
                discovery.Error -= OnDiscoveryError;
                await discovery.DisposeAsync();
                discovery = null;
            }
            if (peer is not null)
            {
                peer.Changed -= OnPeerChanged;
                peer.Error -= OnPeerError;
                await peer.DisposeAsync();
                peer = null;
            }
            await meshTask;
            Engine = new SyncEngine(Engine.PeerId, requestedSession);
            SessionId = requestedSession.ToString();
            isSessionLocked = false;
            isListening = false;
            isOffline = false;
            hadConnection = false;
            Notify(nameof(CanEditSession));
            RefreshConnection();
            return true;
        }
        finally
        {
            SetBusy(false);
        }
    }

    public async Task StartListeningAsync()
    {
        if (!CanStartListening || isDisposed)
        {
            return;
        }
        if (!int.TryParse(LocalPort, out int port) || port < 0 || port > 65535)
        {
            Status = "Локальний порт має бути від 0 до 65535 (0 — вільний порт).";
            return;
        }
        if (!EnsureSession())
        {
            return;
        }

        SetBusy(true);
        var newPeer = new TcpPeer(Engine);
        newPeer.Changed += OnPeerChanged;
        newPeer.Error += OnPeerError;
        try
        {
            await Task.Run(() => newPeer.Start(port), lifetime.Token);
            if (isDisposed)
            {
                newPeer.Changed -= OnPeerChanged;
                newPeer.Error -= OnPeerError;
                await newPeer.DisposeAsync();
                return;
            }
            peer = newPeer;
            isListening = true;
            LocalPort = peer.Port.ToString();
            Status = $"Прослуховування порту {LocalPort}.";
            RefreshConnection();
            await StartDiscoveryAsync();
        }
        catch (Exception exception) when (exception is SocketException || exception is ArgumentException || exception is OperationCanceledException)
        {
            newPeer.Changed -= OnPeerChanged;
            newPeer.Error -= OnPeerError;
            await newPeer.DisposeAsync();
            Status = "Не вдалося відкрити локальний порт. Спробуйте інший або залиште 0.";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task StartDiscoveryAsync(bool includeOtherSessions = false)
    {
        if (discovery is not null)
        {
            discovery.Changed -= OnDiscoveryChanged;
            discovery.Error -= OnDiscoveryError;
            await discovery.DisposeAsync();
            discovery = null;
        }
        var newDiscovery = new PeerDiscovery(Engine.PeerId, Engine.SessionId, includeOtherSessions);
        newDiscovery.Changed += OnDiscoveryChanged;
        newDiscovery.Error += OnDiscoveryError;
        try
        {
            int tcpPort = peer?.Port ?? 0;
            await Task.Run(() =>
            {
                if (tcpPort == 0)
                {
                    newDiscovery.StartBrowsing();
                }
                else
                {
                    newDiscovery.Start(tcpPort);
                }
            }, lifetime.Token);
            if (isDisposed)
            {
                newDiscovery.Changed -= OnDiscoveryChanged;
                newDiscovery.Error -= OnDiscoveryError;
                await newDiscovery.DisposeAsync();
                return;
            }
            discovery = newDiscovery;
            RefreshDiscoveredPeers();
        }
        catch (Exception exception) when (exception is IOException || exception is SocketException || exception is NetworkInformationException
            || exception is ArgumentException || exception is OperationCanceledException)
        {
            newDiscovery.Changed -= OnDiscoveryChanged;
            newDiscovery.Error -= OnDiscoveryError;
            await newDiscovery.DisposeAsync();
            DiscoveryStatus = "Пошук недоступний. Скористайтеся ручним підключенням за IP.";
        }
    }

    private void OnDiscoveryChanged() => Dispatcher.UIThread.Post(RefreshDiscoveredPeers);

    private void OnDiscoveryError(string message)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!isDisposed)
            {
                DiscoveryStatus = "Не вдалося знайти сесії. Ручне підключення за IP доступне.";
            }
        });
    }

    private void RefreshDiscoveredPeers()
    {
        if (isDisposed || discovery is null)
        {
            return;
        }
        Guid? selectedId = SelectedPeer?.PeerId;
        Guid? selectedSessionId = SelectedSession?.SessionId;
        DiscoveredPeers = discovery.GetPeers();
        Notify(nameof(DiscoveredPeers));
        SelectedPeer = DiscoveredPeers.FirstOrDefault(found => found.PeerId == selectedId);
        var sessions = new List<DiscoveredPeer>();
        var sessionIds = new HashSet<Guid>();
        foreach (var found in DiscoveredPeers)
        {
            if (sessionIds.Add(found.SessionId))
            {
                sessions.Add(found);
            }
        }
        AvailableSessions = sessions.ToImmutableArray();
        Notify(nameof(AvailableSessions));
        SelectedSession = AvailableSessions.FirstOrDefault(found => found.SessionId == selectedSessionId);
        DiscoveryStatus = AvailableSessions.IsEmpty ? "Пошук сесій… Можна підключитися вручну." : $"Знайдено сесій: {AvailableSessions.Length}";
        if (isListening && !isOffline && !isBusy && meshTask.IsCompleted)
        {
            meshTask = ConnectMeshAsync();
        }
    }

    private async Task ConnectMeshAsync()
    {
        var currentPeer = peer;
        if (currentPeer is null)
        {
            return;
        }
        foreach (var found in DiscoveredPeers)
        {
            if (isDisposed || isOffline || found.SessionId != Engine.SessionId || currentPeer.IsConnected(found.PeerId))
            {
                continue;
            }
            // Only one side starts discovery connections; manual connections can use either direction.
            if (Engine.PeerId.CompareTo(found.PeerId) > 0)
            {
                continue;
            }
            try
            {
                await currentPeer.ConnectAsync(found.Address, found.TcpPort, lifetime.Token);
            }
            catch (Exception exception) when (exception is IOException || exception is InvalidDataException || exception is SocketException
                || exception is OperationCanceledException || exception is ArgumentException)
            {
            }
        }
    }

    public async Task ConnectDiscoveredAsync()
    {
        if (!CanConnectDiscovered || SelectedPeer is null)
        {
            return;
        }
        RemoteAddress = SelectedPeer.Address;
        RemotePort = SelectedPeer.TcpPort.ToString();
        await ConnectAsync();
    }

    public async Task ConnectAsync()
    {
        if (!CanConnect || peer is null || isDisposed)
        {
            return;
        }
        if (!IPAddress.TryParse(RemoteAddress, out _) || !int.TryParse(RemotePort, out int port) || port < 1 || port > 65535)
        {
            Status = "Вкажіть правильну IP-адресу та порт від 1 до 65535.";
            return;
        }

        SetBusy(true);
        isOffline = false;
        Status = "Підключення…";
        try
        {
            await peer.ConnectAsync(RemoteAddress, port, lifetime.Token);
            Status = "З'єднання встановлено. Зміни передаються автоматично.";
        }
        catch (Exception exception) when (exception is IOException || exception is InvalidDataException || exception is SocketException
            || exception is OperationCanceledException || exception is ArgumentException)
        {
            Status = "Не вдалося підключитися. Перевірте адресу, сесію та доступність іншого пристрою.";
        }
        finally
        {
            RefreshConnection();
            SetBusy(false);
        }
    }

    public void Disconnect()
    {
        isOffline = true;
        peer?.DisconnectAll();
        Status = "Відключено. Малювання офлайн доступне; для синхронізації підключіться знову.";
        RefreshConnection();
    }

    public void ResumeNetworking()
    {
        if (isDisposed || isOffline || peer is null)
        {
            return;
        }
        peer.DisconnectAll(reconnect: true);
        RefreshConnection();
    }

    public bool EnsureSession()
    {
        if (isSessionLocked)
        {
            return true;
        }
        if (!Guid.TryParse(SessionId, out var requestedSession) || requestedSession == Guid.Empty)
        {
            Status = "SessionId має бути непорожнім GUID. Скопіюйте його з іншого вікна.";
            return false;
        }
        if (Engine.SessionId != requestedSession)
        {
            Engine = new SyncEngine(Engine.PeerId, requestedSession);
        }
        isSessionLocked = true;
        Notify(nameof(CanEditSession));
        return true;
    }

    public void PublishOperation(Operation operation)
    {
        outgoing.Writer.TryWrite(operation);
        RefreshConnection();
    }

    private async Task SendOperationsAsync()
    {
        try
        {
            await foreach (var operation in outgoing.Reader.ReadAllAsync(lifetime.Token))
            {
                var currentPeer = peer;
                if (currentPeer is not null)
                {
                    await currentPeer.SendOperationAsync(operation, lifetime.Token);
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
    }

    private void OnPeerChanged() => Dispatcher.UIThread.Post(RefreshConnection);

    private void OnPeerError(string message)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!isDisposed && !isOffline)
            {
                Status = "З'єднання втрачено. Очікуємо повторного підключення.";
            }
        });
    }

    private void RefreshConnection()
    {
        if (isDisposed)
        {
            return;
        }
        ConnectedPeers = peer?.ConnectionCount ?? 0;
        if (ConnectedPeers > 0)
        {
            hadConnection = true;
        }
        Notify(nameof(ConnectedPeers));
        Notify(nameof(ConnectionStatus));
        Notify(nameof(SessionTitle));
        Notify(nameof(CanEditSession));
        DocumentChanged?.Invoke();
    }

    private void SetBusy(bool value)
    {
        isBusy = value;
        Notify(nameof(CanStartListening));
        Notify(nameof(CanConnect));
        Notify(nameof(CanDisconnect));
        Notify(nameof(CanConnectDiscovered));
        Notify(nameof(CanCreateSession));
        Notify(nameof(CanSearchSessions));
        Notify(nameof(CanJoinSession));
    }

    private void Notify([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public async ValueTask DisposeAsync()
    {
        if (isDisposed)
        {
            return;
        }
        isDisposed = true;
        outgoing.Writer.TryComplete();
        await lifetime.CancelAsync();
        if (discovery is not null)
        {
            discovery.Changed -= OnDiscoveryChanged;
            discovery.Error -= OnDiscoveryError;
            await discovery.DisposeAsync();
        }
        if (peer is not null)
        {
            peer.Changed -= OnPeerChanged;
            peer.Error -= OnPeerError;
            await peer.DisposeAsync();
        }
        await meshTask;
        await sendingTask;
        lifetime.Dispose();
    }
}
