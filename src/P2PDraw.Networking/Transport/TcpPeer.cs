using System.Collections.Immutable;
using System.Net;
using System.Net.Sockets;
using P2PDraw.Core.Operations;
using P2PDraw.Core.Sync;
using P2PDraw.Networking.Protocol;

namespace P2PDraw.Networking.Transport;

public sealed class TcpPeer : IAsyncDisposable
{
    public const int ProtocolVersion = 1;
    private readonly object stateLock = new();
    private readonly SyncEngine engine;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Dictionary<Guid, Connection> connections = [];
    private readonly HashSet<Task> backgroundTasks = [];
    private readonly Dictionary<(string Host, int Port), Guid> reconnectTargets = [];
    private readonly Task reconnectTask;
    private bool isManuallyDisconnected;
    private TcpListener? listener;
    private Task? acceptTask;

    public event Action? Changed;
    public event Action<string>? Error;
    public int Port => (listener?.LocalEndpoint as IPEndPoint)?.Port ?? 0;
    public int ConnectionCount
    {
        get
        {
            lock (stateLock)
            {
                return connections.Count;
            }
        }
    }

    public TcpPeer(SyncEngine engine)
    {
        this.engine = engine;
        reconnectTask = ReconnectAsync();
    }

    public void Start(int port, IPAddress? address = null)
    {
        ObjectDisposedException.ThrowIf(lifetime.IsCancellationRequested, this);
        if (listener is not null)
        {
            throw new InvalidOperationException("Listener already started.");
        }

        var newListener = new TcpListener(address ?? IPAddress.Any, port);
        try
        {
            newListener.Start();
        }
        catch
        {
            newListener.Stop();
            throw;
        }
        listener = newListener;
        acceptTask = AcceptConnectionsAsync();
    }

    public Task<Guid> ConnectAsync(string host, int port, CancellationToken cancellationToken = default)
    {
        lock (stateLock)
        {
            isManuallyDisconnected = false;
        }

        return ConnectCoreAsync(host, port, Guid.Empty, cancellationToken);
    }

    private async Task<Guid> ConnectCoreAsync(string host, int port, Guid expectedPeerId, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var client = new TcpClient { NoDelay = true };
        try
        {
            await client.ConnectAsync(host, port, timeout.Token);
            var stream = client.GetStream();
            var hello = new HelloMessage(engine.PeerId, engine.SessionId, ProtocolVersion);
            await MessageFraming.WriteAsync<NetworkMessage>(stream, hello, timeout.Token);
            var response = await MessageFraming.ReadAsync<NetworkMessage>(stream, timeout.Token);
            if (response is not WelcomeMessage welcome)
            {
                throw new InvalidDataException("Expected WELCOME.");
            }

            ValidateHello(welcome.PeerId, welcome.SessionId, welcome.ProtocolVersion);
            if (expectedPeerId != Guid.Empty && welcome.PeerId != expectedPeerId)
            {
                throw new InvalidDataException("Reconnected endpoint has a different PeerId.");
            }

            var connection = new Connection(client, welcome.PeerId, true);
            if (RegisterConnection(connection, (host, port), expectedPeerId))
            {
                TrackBackgroundTask(RunConnectionAsync(connection));
            }
            else
            {
                client.Dispose();
            }

            return welcome.PeerId;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public async Task SendOperationAsync(Operation operation, CancellationToken cancellationToken = default)
    {
        operation.Validate();
        Connection[] targets;
        lock (stateLock)
        {
            targets = connections.Values.ToArray();
        }

        var sendingTasks = new List<Task>();
        foreach (var connection in targets)
        {
            sendingTasks.Add(SendAsync(connection, new OperationMessage(operation), cancellationToken));
        }
        await Task.WhenAll(sendingTasks);
    }

    public void DisconnectAll(bool reconnect = false)
    {
        lock (stateLock)
        {
            if (!reconnect)
            {
                isManuallyDisconnected = true;
                reconnectTargets.Clear();
            }
            foreach (var connection in connections.Values)
            {
                connection.Client.Dispose();
            }

            connections.Clear();
        }
        Changed?.Invoke();
    }

    private async Task AcceptConnectionsAsync()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                var client = await listener!.AcceptTcpClientAsync(lifetime.Token);
                client.NoDelay = true;
                TrackBackgroundTask(AcceptConnectionAsync(client));
            }
        }
        catch (Exception exception) when (IsConnectionError(exception))
        {
            if (!lifetime.IsCancellationRequested)
            {
                Error?.Invoke(exception.Message);
            }
        }
    }

    private async Task AcceptConnectionAsync(TcpClient client)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var stream = client.GetStream();
            var message = await MessageFraming.ReadAsync<NetworkMessage>(stream, timeout.Token);
            if (message is not HelloMessage hello)
            {
                throw new InvalidDataException("Expected HELLO.");
            }

            ValidateHello(hello.PeerId, hello.SessionId, hello.ProtocolVersion);
            lock (stateLock)
            {
                if (isManuallyDisconnected)
                {
                    return;
                }
            }
            var welcome = new WelcomeMessage(engine.PeerId, engine.SessionId, ProtocolVersion);
            await MessageFraming.WriteAsync<NetworkMessage>(stream, welcome, timeout.Token);
            var connection = new Connection(client, hello.PeerId, false);
            if (RegisterConnection(connection))
            {
                await RunConnectionAsync(connection);
            }
        }
        catch (Exception exception) when (IsConnectionError(exception))
        {
            if (!lifetime.IsCancellationRequested)
            {
                Error?.Invoke(exception.Message);
            }
        }
        finally
        {
            client.Dispose();
        }
    }

    private bool RegisterConnection(Connection connection, (string Host, int Port)? target = null, Guid expectedPeerId = default)
    {
        lock (stateLock)
        {
            if (lifetime.IsCancellationRequested || isManuallyDisconnected)
            {
                return false;
            }

            if (target.HasValue)
            {
                var endpoint = target.Value;
                if (expectedPeerId != Guid.Empty && reconnectTargets.GetValueOrDefault(endpoint) != expectedPeerId)
                {
                    return false;
                }

                reconnectTargets[endpoint] = connection.PeerId;
            }
            if (connections.TryGetValue(connection.PeerId, out var existing))
            {
                // Both ends keep the same direction when they connect simultaneously.
                bool preferOutgoing = engine.PeerId.CompareTo(connection.PeerId) < 0;
                if (existing.IsOutgoing == preferOutgoing || connection.IsOutgoing != preferOutgoing)
                {
                    return false;
                }

                existing.Client.Dispose();
            }
            connections[connection.PeerId] = connection;
        }
        Changed?.Invoke();
        return true;
    }

    private async Task RunConnectionAsync(Connection connection)
    {
        connection.IsSyncPending = true;
        var readingTask = ReceiveMessagesAsync(connection);
        await SendAsync(connection, CreateSyncRequest(), lifetime.Token);
        await readingTask;
    }

    private SyncRequestMessage CreateSyncRequest()
    {
        var knownIds = engine.GetOperations().Select(operation => operation.Stamp.OperationId).ToImmutableArray();
        var request = new SyncRequestMessage(knownIds);
        if (!MessageFraming.FitsInFrame<NetworkMessage>(request))
        {
            // An empty request safely replays the log when the ID list no longer fits.
            return new SyncRequestMessage([]);
        }
        return request;
    }

    private async Task RespondToSyncAsync(Connection connection, SyncRequestMessage request)
    {
        if (request.KnownOperationIds.IsEmpty)
        {
            var snapshot = engine.CreateSnapshot();
            var message = new SnapshotMessage(snapshot);
            if (MessageFraming.FitsInFrame<NetworkMessage>(message))
            {
                await SendAsync(connection, message, lifetime.Token);
            }
            else
            {
                // Replay the same frozen log as separate frames, including removals and pending points.
                foreach (var operation in snapshot.Operations)
                {
                    await SendAsync(connection, new OperationMessage(operation), lifetime.Token);
                }
            }
            var snapshotOperationIds = snapshot.Operations.Select(operation => operation.Stamp.OperationId);
            var missingOperations = engine.GetMissingOperations(snapshotOperationIds);
            foreach (var operation in missingOperations)
            {
                await SendAsync(connection, new OperationMessage(operation), lifetime.Token);
            }
        }
        else
        {
            foreach (var operation in engine.GetMissingOperations(request.KnownOperationIds))
            {
                await SendAsync(connection, new OperationMessage(operation), lifetime.Token);
            }
        }
        await SendAsync(connection, new SyncCompleteMessage(), lifetime.Token);
    }

    private async Task ReceiveMessagesAsync(Connection connection)
    {
        try
        {
            while (true)
            {
                var message = await MessageFraming.ReadAsync<NetworkMessage>(connection.Client.GetStream(), lifetime.Token);
                if (message is null)
                {
                    break;
                }

                switch (message)
                {
                    case OperationMessage operationMessage:
                        if (operationMessage.Operation is null)
                        {
                            throw new InvalidDataException("Unexpected or invalid message.");
                        }
                        if (engine.Receive(operationMessage.Operation))
                        {
                            Changed?.Invoke();
                        }

                        if (!connection.IsSyncPending && !engine.GetPendingStrokeIds().IsEmpty)
                        {
                            connection.IsSyncPending = true;
                            TrackBackgroundTask(SendAsync(connection, CreateSyncRequest(), lifetime.Token));
                        }
                        break;
                    case SyncRequestMessage request:
                        if (request.KnownOperationIds.IsDefault || request.KnownOperationIds.Any(operationId => operationId == Guid.Empty))
                        {
                            throw new InvalidDataException("Unexpected or invalid message.");
                        }
                        // Send in parallel with readingTask so two large snapshots cannot block each other.
                        TrackBackgroundTask(RespondToSyncAsync(connection, request));
                        break;
                    case SnapshotMessage snapshotMessage:
                        if (snapshotMessage.Snapshot is null)
                        {
                            throw new InvalidDataException("Unexpected or invalid message.");
                        }
                        engine.MergeSnapshot(snapshotMessage.Snapshot);
                        Changed?.Invoke();
                        break;
                    case SyncCompleteMessage:
                        connection.IsSyncPending = false;
                        break;
                    default:
                        throw new InvalidDataException("Unexpected or invalid message.");
                }
            }
        }
        catch (Exception exception) when (IsConnectionError(exception))
        {
            if (!lifetime.IsCancellationRequested)
            {
                Error?.Invoke(exception.Message);
            }
        }
        finally
        {
            connection.Client.Dispose();
            lock (stateLock)
            {
                if (connections.GetValueOrDefault(connection.PeerId) == connection)
                {
                    connections.Remove(connection.PeerId);
                }
            }
            Changed?.Invoke();
        }
    }

    private async Task SendAsync(Connection connection, NetworkMessage message, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            await connection.SendAsync(message, timeout.Token);
        }
        catch (Exception exception) when (IsConnectionError(exception))
        {
            connection.Client.Dispose();
            if (!lifetime.IsCancellationRequested)
            {
                Error?.Invoke(exception.Message);
            }
        }
    }

    private void ValidateHello(Guid peerId, Guid sessionId, int version)
    {
        if (peerId == Guid.Empty || peerId == engine.PeerId)
        {
            throw new InvalidDataException("Invalid or duplicate PeerId.");
        }

        if (sessionId != engine.SessionId)
        {
            throw new InvalidDataException("SessionId does not match.");
        }

        if (version != ProtocolVersion)
        {
            throw new InvalidDataException("Protocol version does not match.");
        }
    }

    private async Task ReconnectAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(lifetime.Token))
            {
                var targets = new List<KeyValuePair<(string Host, int Port), Guid>>();
                lock (stateLock)
                {
                    foreach (var target in reconnectTargets)
                    {
                        if (!connections.ContainsKey(target.Value))
                        {
                            targets.Add(target);
                        }
                    }
                }
                foreach (var target in targets)
                {
                    var endpoint = target.Key;
                    Guid expectedPeerId = target.Value;
                    try
                    {
                        await ConnectCoreAsync(endpoint.Host, endpoint.Port, expectedPeerId, lifetime.Token);
                    }
                    catch (Exception exception) when (IsConnectionError(exception))
                    {
                    }
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
    }

    private void TrackBackgroundTask(Task task)
    {
        lock (stateLock)
        {
            backgroundTasks.Add(task);
        }

        _ = task.ContinueWith(completed =>
        {
            lock (stateLock)
            {
                backgroundTasks.Remove(completed);
            }
        }, TaskScheduler.Default);
    }

    private static bool IsConnectionError(Exception exception)
    {
        return exception is IOException || exception is SocketException || exception is OperationCanceledException
            || exception is ObjectDisposedException || exception is ArgumentException;
    }

    public async ValueTask DisposeAsync()
    {
        await lifetime.CancelAsync();
        listener?.Stop();
        DisconnectAll();
        if (acceptTask is not null)
        {
            await acceptTask;
        }

        await reconnectTask;
        Task[] pending;
        lock (stateLock)
        {
            pending = backgroundTasks.ToArray();
        }

        await Task.WhenAll(pending);
    }

    private sealed class Connection
    {
        private readonly SemaphoreSlim sendLock = new(1, 1);
        public TcpClient Client { get; }
        public Guid PeerId { get; }
        public bool IsOutgoing { get; }
        public bool IsSyncPending { get; set; }

        public Connection(TcpClient client, Guid peerId, bool isOutgoing)
        {
            Client = client;
            PeerId = peerId;
            IsOutgoing = isOutgoing;
        }

        public async Task SendAsync(NetworkMessage message, CancellationToken cancellationToken)
        {
            await sendLock.WaitAsync(cancellationToken);
            try
            {
                await MessageFraming.WriteAsync(Client.GetStream(), message, cancellationToken);
            }
            finally
            {
                sendLock.Release();
            }
        }
    }
}
