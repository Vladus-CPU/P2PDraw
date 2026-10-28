using System.Collections.Immutable;
using System.ComponentModel;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Avalonia.Threading;
using P2PDraw.Core.Models;
using P2PDraw.Core.Operations;
using P2PDraw.Core.Sync;
using P2PDraw.Networking.Discovery;
using P2PDraw.Networking.Transport;

namespace P2PDraw.App.ViewModels;

public sealed class MainWindowViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private readonly Channel<Operation> outgoing = Channel.CreateUnbounded<Operation>();
    private readonly Task sendingTask;
    private SyncEngine engine;
    private TcpPeer? peer;
    private PeerDiscovery? discovery;
    private DiscoveredPeer? selectedPeer;
    private string discoveryStatus = "Пошук почнеться після запуску прослуховування.";
    private Guid? activeStrokeId;
    private string sessionId = Guid.NewGuid().ToString();
    private string localPort = "5101";
    private string remoteAddress = "127.0.0.1";
    private string remotePort = "5102";
    private string status = "Можна малювати офлайн або підключити інший вузол.";
    private double brushWidth = 3;
    private bool isSessionLocked;
    private bool isListening;
    private bool isBusy;
    private bool isOffline;
    private bool isDisposed;

    public event PropertyChangedEventHandler? PropertyChanged;
    public ImmutableArray<Stroke> Strokes { get; private set; } = [];
    public int StrokeCount => Strokes.Length;
    public int OperationCount { get; private set; }
    public int ConnectedPeers { get; private set; }
    public ImmutableArray<DiscoveredPeer> DiscoveredPeers { get; private set; } = [];
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
    public uint BrushColor { get; set; } = 0xFF253449;
    public bool CanEditSession => !isSessionLocked;
    public bool CanStartListening => !isListening && !isBusy;
    public bool CanConnect => isListening && !isBusy;
    public bool CanDisconnect => isListening && !isBusy;
    public string ConnectionStatus
    {
        get
        {
            if (ConnectedPeers > 0)
            {
                return "З'єднано";
            }
            if (isOffline)
            {
                return "Офлайн";
            }
            return isListening ? "Очікування вузла" : "Не підключено";
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

    public double BrushWidth
    {
        get => brushWidth;
        set
        {
            brushWidth = value;
            Notify();
        }
    }

    public string Status
    {
        get => status;
        private set
        {
            status = value;
            Notify();
        }
    }

    public MainWindowViewModel(string[]? args = null)
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
        engine = new SyncEngine(Guid.NewGuid(), initialSession);
        sendingTask = Task.Run(SendOperationsAsync);
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
        var newPeer = new TcpPeer(engine);
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
            RefreshDocument();
            await StartDiscoveryAsync();
        }
        catch (Exception exception) when (exception is SocketException || exception is ArgumentException || exception is OperationCanceledException)
        {
            newPeer.Changed -= OnPeerChanged;
            newPeer.Error -= OnPeerError;
            await newPeer.DisposeAsync();
            Status = $"Не вдалося почати прослуховування: {exception.Message}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task StartDiscoveryAsync()
    {
        var newDiscovery = new PeerDiscovery(engine.PeerId, engine.SessionId);
        newDiscovery.Changed += OnDiscoveryChanged;
        newDiscovery.Error += OnDiscoveryError;
        try
        {
            int tcpPort = peer!.Port;
            await Task.Run(() => newDiscovery.Start(tcpPort), lifetime.Token);
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
            DiscoveryStatus = $"Пошук недоступний. Підключіться за IP: {exception.Message}";
        }
    }

    private void OnDiscoveryChanged() => Dispatcher.UIThread.Post(RefreshDiscoveredPeers);

    private void OnDiscoveryError(string message)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!isDisposed)
            {
                DiscoveryStatus = $"Пошук: {message}. Ручне TCP-підключення доступне.";
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
        DiscoveredPeers = discovery.GetPeers();
        Notify(nameof(DiscoveredPeers));
        SelectedPeer = DiscoveredPeers.FirstOrDefault(found => found.PeerId == selectedId);
        DiscoveryStatus = DiscoveredPeers.IsEmpty ? "Шукаємо вузли цієї сесії. Можна підключитися за IP." : $"Знайдено вузлів: {DiscoveredPeers.Length}";
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
        catch (Exception exception) when (exception is IOException || exception is SocketException || exception is OperationCanceledException || exception is ArgumentException)
        {
            Status = $"Не вдалося підключитися: {exception.Message}";
        }
        finally
        {
            RefreshDocument();
            SetBusy(false);
        }
    }

    public void Disconnect()
    {
        isOffline = true;
        peer?.DisconnectAll();
        Status = "Відключено. Малювання офлайн доступне; для синхронізації підключіться знову.";
        RefreshDocument();
    }

    public void ResumeNetworking()
    {
        if (isDisposed || isOffline || peer is null)
        {
            return;
        }
        peer.DisconnectAll(reconnect: true);
        RefreshDocument();
    }

    public bool BeginStroke(StrokePoint point)
    {
        if (isDisposed || !EnsureSession())
        {
            return false;
        }
        EndStroke();
        var begin = engine.BeginStroke(BrushColor, BrushWidth);
        activeStrokeId = begin.StrokeId;
        PublishOperation(begin);
        AppendStroke(point);
        return true;
    }

    public void AppendStroke(StrokePoint point)
    {
        if (activeStrokeId is null || isDisposed)
        {
            return;
        }
        try
        {
            PublishOperation(engine.AppendStroke(activeStrokeId.Value, [point]));
        }
        catch (InvalidOperationException)
        {
            // A remote clear can remove the stroke while the pointer is still captured.
            activeStrokeId = null;
            RefreshDocument();
        }
    }

    public void EndStroke()
    {
        if (activeStrokeId is null || isDisposed)
        {
            return;
        }
        try
        {
            PublishOperation(engine.EndStroke(activeStrokeId.Value));
        }
        catch (InvalidOperationException)
        {
            RefreshDocument();
        }
        activeStrokeId = null;
    }

    public void ClearCanvas()
    {
        if (isDisposed || !EnsureSession())
        {
            return;
        }
        EndStroke();
        PublishOperation(engine.ClearCanvas());
        Status = "Відомі штрихи видалено.";
    }

    public Snapshot GetSnapshot() => engine.CreateSnapshot();

    private bool EnsureSession()
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
        if (engine.SessionId != requestedSession)
        {
            engine = new SyncEngine(engine.PeerId, requestedSession);
        }
        isSessionLocked = true;
        Notify(nameof(CanEditSession));
        return true;
    }

    private void PublishOperation(Operation operation)
    {
        outgoing.Writer.TryWrite(operation);
        RefreshDocument();
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

    private void OnPeerChanged() => Dispatcher.UIThread.Post(RefreshDocument);

    private void OnPeerError(string message)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!isDisposed && !isOffline)
            {
                Status = $"Мережа: {message}";
            }
        });
    }

    private void RefreshDocument()
    {
        if (isDisposed)
        {
            return;
        }
        var snapshot = engine.CreateSnapshot();
        Strokes = snapshot.Strokes;
        OperationCount = snapshot.Operations.Length;
        ConnectedPeers = peer?.ConnectionCount ?? 0;
        Notify(nameof(Strokes));
        Notify(nameof(StrokeCount));
        Notify(nameof(OperationCount));
        Notify(nameof(ConnectedPeers));
        Notify(nameof(ConnectionStatus));
    }

    private void SetBusy(bool value)
    {
        isBusy = value;
        Notify(nameof(CanStartListening));
        Notify(nameof(CanConnect));
        Notify(nameof(CanDisconnect));
        Notify(nameof(CanConnectDiscovered));
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
        EndStroke();
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
        await sendingTask;
        lifetime.Dispose();
    }
}
