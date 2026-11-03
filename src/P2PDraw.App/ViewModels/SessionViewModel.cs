using System.Collections.Immutable;
using System.ComponentModel;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Avalonia.Threading;
using P2PDraw.App.Session;
using P2PDraw.Core.Operations;
using P2PDraw.Core.Sync;
using P2PDraw.Networking.Discovery;

namespace P2PDraw.App.ViewModels;

public sealed class SessionViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private readonly EditorSession editorSession;
    private SessionConnection Connection => editorSession.Connection;
    private DiscoveredPeer? selectedPeer;
    private DiscoveredPeer? selectedSession;
    private bool hadConnection;
    private string discoveryStatus = "Пошук почнеться після запуску прослуховування.";
    private string sessionId = Guid.NewGuid().ToString();
    private string localPort = "5101";
    private string remoteAddress = "127.0.0.1";
    private string remotePort = "5102";
    private string status = "Можна малювати офлайн або підключити інший вузол.";
    private bool isListening;
    private bool isBusy;
    private bool isDisposed;

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? DocumentChanged;
    public SyncEngine Engine => editorSession.Engine;
    public int ConnectedPeers { get; private set; }
    public ImmutableArray<DiscoveredPeer> DiscoveredPeers { get; private set; } = [];
    public ImmutableArray<DiscoveredPeer> AvailableSessions { get; private set; } = [];
    public bool CanCreateSession => CanStartListening;
    public bool CanSearchSessions => !isBusy && !isDisposed;
    public bool CanJoinSession => SelectedSession is not null && !isBusy && !isDisposed;
    public string SessionTitle => editorSession.IsLocked ? $"Сесія {Engine.SessionId.ToString()[..8]}" : "Локальний документ";

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
    public bool CanEditSession => !editorSession.IsLocked || (Engine.GetOperations().IsEmpty && ConnectedPeers == 0);
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
            if (Connection.IsOffline)
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
        editorSession = new EditorSession(initialSession);
        editorSession.Replaced += OnSessionReplaced;
        Connection.Changed += OnPeerChanged;
        Connection.Error += OnPeerError;
        Connection.DiscoveryChanged += OnDiscoveryChanged;
        Connection.DiscoveryError += OnDiscoveryError;
    }

    public async Task CreateSessionAsync()
    {
        if (!CanCreateSession)
        {
            return;
        }
        if (Engine.GetOperations().IsEmpty && !editorSession.IsLocked)
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
        var replacement = editorSession.PrepareJoin(requestedSession);
        if (replacement is null)
        {
            Status = "Документ уже містить зміни. Іншу сесію не відкрито, щоб не втратити малюнок.";
            return false;
        }
        if (!ReferenceEquals(Engine, replacement))
        {
            await ReplaceSessionAsync(replacement);
        }
        return true;
    }

    public async Task<bool> OpenAsNewSessionAsync(Snapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.SessionId == Guid.Empty)
        {
            throw new ArgumentException("Document session ID is missing.");
        }
        if (isDisposed || isBusy)
        {
            return false;
        }
        var replacement = await editorSession.RestoreAsync(snapshot);
        if (isDisposed || isBusy)
        {
            return false;
        }
        await ReplaceSessionAsync(replacement);
        LocalPort = "0";
        Status = "Документ відкрито як нову сесію. Можна працювати офлайн або створити підключення.";
        return true;
    }

    private async Task ReplaceSessionAsync(SyncEngine replacement)
    {
        SetBusy(true);
        try
        {
            await editorSession.ReplaceAsync(replacement);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void OnSessionReplaced()
    {
        SessionId = Engine.SessionId.ToString();
        isListening = false;
        hadConnection = false;
        DiscoveredPeers = [];
        AvailableSessions = [];
        SelectedPeer = null;
        SelectedSession = null;
        Notify(nameof(DiscoveredPeers));
        Notify(nameof(AvailableSessions));
        Notify(nameof(CanEditSession));
        RefreshConnection();
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
        try
        {
            if (!await Connection.StartListeningAsync(port) || isDisposed)
            {
                return;
            }
            isListening = true;
            LocalPort = Connection.Port.ToString();
            Status = $"Прослуховування порту {LocalPort}.";
            RefreshConnection();
            await StartDiscoveryAsync();
        }
        catch (Exception exception) when (exception is SocketException || exception is ArgumentException || exception is OperationCanceledException)
        {
            Status = "Не вдалося відкрити локальний порт. Спробуйте інший або залиште 0.";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task StartDiscoveryAsync(bool includeOtherSessions = false)
    {
        try
        {
            if (await Connection.StartDiscoveryAsync(includeOtherSessions))
            {
                RefreshDiscoveredPeers();
            }
        }
        catch (Exception exception) when (exception is IOException || exception is SocketException || exception is NetworkInformationException
            || exception is ArgumentException || exception is OperationCanceledException)
        {
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
        if (isDisposed || !Connection.HasDiscovery)
        {
            return;
        }
        Guid? selectedId = SelectedPeer?.PeerId;
        Guid? selectedSessionId = SelectedSession?.SessionId;
        DiscoveredPeers = Connection.GetDiscoveredPeers();
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
        if (isListening && !Connection.IsOffline && !isBusy)
        {
            Connection.ConnectMesh(DiscoveredPeers);
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
        if (!CanConnect || !Connection.HasPeer || isDisposed)
        {
            return;
        }
        if (!IPAddress.TryParse(RemoteAddress, out _) || !int.TryParse(RemotePort, out int port) || port < 1 || port > 65535)
        {
            Status = "Вкажіть правильну IP-адресу та порт від 1 до 65535.";
            return;
        }

        SetBusy(true);
        Status = "Підключення…";
        try
        {
            await Connection.ConnectAsync(RemoteAddress, port);
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
        Connection.Disconnect();
        Status = "Відключено. Малювання офлайн доступне; для синхронізації підключіться знову.";
        RefreshConnection();
    }

    public void ResumeNetworking()
    {
        if (isDisposed || Connection.IsOffline || !Connection.HasPeer)
        {
            return;
        }
        Connection.ResumeNetworking();
        RefreshConnection();
    }

    public bool EnsureSession()
    {
        if (isDisposed || editorSession.IsReplacing)
        {
            return false;
        }
        if (editorSession.IsLocked)
        {
            return true;
        }
        if (!Guid.TryParse(SessionId, out var requestedSession) || requestedSession == Guid.Empty)
        {
            Status = "SessionId має бути непорожнім GUID. Скопіюйте його з іншого вікна.";
            return false;
        }
        if (!editorSession.EnsureSession(requestedSession))
        {
            return false;
        }
        Notify(nameof(CanEditSession));
        return true;
    }

    public void PublishOperation(Operation operation)
    {
        Connection.PublishOperation(operation);
        RefreshConnection();
    }

    private void OnPeerChanged() => Dispatcher.UIThread.Post(RefreshConnection);

    private void OnPeerError(string message)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!isDisposed && !Connection.IsOffline && Connection.HasPeer && Connection.ConnectionCount == 0)
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
        int previousCount = ConnectedPeers;
        ConnectedPeers = Connection.ConnectionCount;
        if (ConnectedPeers > 0)
        {
            hadConnection = true;
            if (previousCount == 0)
            {
                Status = "З'єднання встановлено. Зміни передаються автоматично.";
            }
        }
        else if (previousCount > 0 && !Connection.IsOffline)
        {
            Status = "З'єднання втрачено. Очікуємо повторного підключення.";
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
        editorSession.Replaced -= OnSessionReplaced;
        Connection.Changed -= OnPeerChanged;
        Connection.Error -= OnPeerError;
        Connection.DiscoveryChanged -= OnDiscoveryChanged;
        Connection.DiscoveryError -= OnDiscoveryError;
        await editorSession.DisposeAsync();
    }
}
