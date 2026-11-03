using System.Collections.Immutable;
using System.Net.Sockets;
using System.Threading.Channels;
using P2PDraw.Core.Operations;
using P2PDraw.Core.Sync;
using P2PDraw.Networking.Discovery;
using P2PDraw.Networking.Transport;

namespace P2PDraw.App.Session;

public sealed class SessionConnection : IAsyncDisposable
{
    private readonly Func<SyncEngine> getEngine;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Channel<(Guid SessionId, Operation Operation)> outgoing = Channel.CreateUnbounded<(Guid, Operation)>();
    private readonly Task sendingTask;
    private TcpPeer? peer;
    private PeerDiscovery? discovery;
    private Task meshTask = Task.CompletedTask;
    private bool isDisposed;
    private bool isStopping;

    public event Action? Changed;
    public event Action<string>? Error;
    public event Action? DiscoveryChanged;
    public event Action<string>? DiscoveryError;
    public int Port => peer?.Port ?? 0;
    public int ConnectionCount => peer?.ConnectionCount ?? 0;
    public bool HasPeer => peer is not null;
    public bool HasDiscovery => discovery is not null;
    public bool IsOffline { get; private set; }

    public SessionConnection(Func<SyncEngine> getEngine)
    {
        this.getEngine = getEngine;
        sendingTask = Task.Run(SendOperationsAsync);
    }

    public async Task<bool> StartListeningAsync(int port)
    {
        var newPeer = new TcpPeer(getEngine());
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
                return false;
            }
            peer = newPeer;
            return true;
        }
        catch
        {
            newPeer.Changed -= OnPeerChanged;
            newPeer.Error -= OnPeerError;
            await newPeer.DisposeAsync();
            throw;
        }
    }

    public async Task<bool> StartDiscoveryAsync(bool includeOtherSessions = false)
    {
        if (discovery is not null)
        {
            discovery.Changed -= OnDiscoveryChanged;
            discovery.Error -= OnDiscoveryError;
            await discovery.DisposeAsync();
            discovery = null;
        }
        var engine = getEngine();
        var newDiscovery = new PeerDiscovery(engine.PeerId, engine.SessionId, includeOtherSessions);
        newDiscovery.Changed += OnDiscoveryChanged;
        newDiscovery.Error += OnDiscoveryError;
        try
        {
            int tcpPort = Port;
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
                return false;
            }
            discovery = newDiscovery;
            return true;
        }
        catch
        {
            newDiscovery.Changed -= OnDiscoveryChanged;
            newDiscovery.Error -= OnDiscoveryError;
            await newDiscovery.DisposeAsync();
            throw;
        }
    }

    public ImmutableArray<DiscoveredPeer> GetDiscoveredPeers() => discovery?.GetPeers() ?? [];

    public async Task ConnectAsync(string address, int port)
    {
        var currentPeer = peer;
        if (isDisposed || currentPeer is null)
        {
            return;
        }
        IsOffline = false;
        await currentPeer.ConnectAsync(address, port, lifetime.Token);
    }

    public void ConnectMesh(ImmutableArray<DiscoveredPeer> peers)
    {
        if (isDisposed || isStopping || IsOffline || !meshTask.IsCompleted)
        {
            return;
        }
        meshTask = ConnectMeshAsync(peers);
    }

    private async Task ConnectMeshAsync(ImmutableArray<DiscoveredPeer> peers)
    {
        var currentPeer = peer;
        if (currentPeer is null)
        {
            return;
        }
        foreach (var found in peers)
        {
            var engine = getEngine();
            if (isDisposed || isStopping || IsOffline || found.SessionId != engine.SessionId || currentPeer.IsConnected(found.PeerId))
            {
                continue;
            }
            // Only one side starts discovery connections; manual connections can use either direction.
            if (engine.PeerId.CompareTo(found.PeerId) > 0)
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

    public void Disconnect()
    {
        IsOffline = true;
        peer?.DisconnectAll();
    }

    public void ResumeNetworking()
    {
        if (isDisposed || IsOffline || peer is null)
        {
            return;
        }
        peer.DisconnectAll(reconnect: true);
    }

    public void PublishOperation(Operation operation)
    {
        outgoing.Writer.TryWrite((getEngine().SessionId, operation));
    }

    private async Task SendOperationsAsync()
    {
        try
        {
            await foreach (var item in outgoing.Reader.ReadAllAsync(lifetime.Token))
            {
                var currentPeer = peer;
                if (currentPeer is not null && item.SessionId == getEngine().SessionId)
                {
                    await currentPeer.SendOperationAsync(item.Operation, lifetime.Token);
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
    }

    public async Task StopAsync()
    {
        isStopping = true;
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
            IsOffline = false;
        }
        finally
        {
            isStopping = false;
        }
    }

    private void OnPeerChanged() => Changed?.Invoke();
    private void OnPeerError(string message) => Error?.Invoke(message);
    private void OnDiscoveryChanged() => DiscoveryChanged?.Invoke();
    private void OnDiscoveryError(string message) => DiscoveryError?.Invoke(message);

    public async ValueTask DisposeAsync()
    {
        if (isDisposed)
        {
            return;
        }
        isDisposed = true;
        outgoing.Writer.TryComplete();
        await lifetime.CancelAsync();
        await StopAsync();
        await sendingTask;
        lifetime.Dispose();
    }
}
