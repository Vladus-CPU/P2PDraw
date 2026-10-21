using P2PDraw.Core.Operations;
using P2PDraw.Core.Sync;
using P2PDraw.Networking.Protocol;
using System.Collections.Immutable;
using System.Net;
using System.Net.Sockets;

namespace P2PDraw.Networking.Transport;

public sealed class TcpPeer : IAsyncDisposable
{
    public const int ProtocolVersion = 1;
    private readonly object gate = new();
    private readonly SyncEngine engine;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Dictionary<Guid, Connection> connections = [];
    private readonly HashSet<Task> tasks = [];
    private readonly Dictionary<(string Host, int Port), Guid> reconnectTargets = [];
    private readonly Task reconnectTask;
    private bool paused;
    private TcpListener? listener;
    private Task? acceptTask;

    public event Action? Changed;
    public event Action<string>? Error;
    public int Port => (listener?.LocalEndpoint as IPEndPoint)?.Port ?? 0;
    public int ConnectionCount
    {
        get { lock (gate) return connections.Count; }
    }

    public TcpPeer(SyncEngine engine)
    {
        this.engine = engine;
        reconnectTask = ReconnectAsync();
    }

    public void Start(int port, IPAddress? address = null)
    {
        ObjectDisposedException.ThrowIf(lifetime.IsCancellationRequested, this);
        if (listener is not null) throw new InvalidOperationException("Listener already started.");
        listener = new TcpListener(address ?? IPAddress.Any, port);
        listener.Start();
        acceptTask = AcceptAsync();
    }

    public Task<Guid> ConnectAsync(string host, int port, CancellationToken cancellationToken = default)
    {
        lock (gate) paused = false;
        return ConnectCoreAsync(host, port, Guid.Empty, cancellationToken);
    }

    private async Task<Guid> ConnectCoreAsync(string host, int port, Guid expectedPeer, CancellationToken cancellationToken)
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
            if (response is not WelcomeMessage welcome) throw new InvalidDataException("Expected WELCOME.");
            ValidateHello(welcome.PeerId, welcome.SessionId, welcome.ProtocolVersion);
            if (expectedPeer != Guid.Empty && welcome.PeerId != expectedPeer) throw new InvalidDataException("Reconnected endpoint has a different PeerId.");
            var connection = new Connection(client, welcome.PeerId, true);
            if (Register(connection, (host, port), expectedPeer)) Track(RunConnectionAsync(connection));
            else client.Dispose();
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
        lock (gate) targets = connections.Values.ToArray();
        var sendingTasks = new List<Task>();
        foreach (var connection in targets)
        {
            sendingTasks.Add(SendAsync(connection, new OperationMessage(operation), cancellationToken));
        }
        await Task.WhenAll(sendingTasks);
    }

    public void DisconnectAll(bool reconnect = false)
    {
        lock (gate)
        {
            if (!reconnect)
            {
                paused = true;
                reconnectTargets.Clear();
            }
            foreach (var connection in connections.Values) connection.Client.Dispose();
            connections.Clear();
        }
        Changed?.Invoke();
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                var client = await listener!.AcceptTcpClientAsync(lifetime.Token);
                client.NoDelay = true;
                Track(AcceptConnectionAsync(client));
            }
        }
        catch (Exception exception) when (IsConnectionError(exception))
        {
            if (!lifetime.IsCancellationRequested) Error?.Invoke(exception.Message);
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
            if (message is not HelloMessage hello) throw new InvalidDataException("Expected HELLO.");
            ValidateHello(hello.PeerId, hello.SessionId, hello.ProtocolVersion);
            lock (gate)
            {
                if (paused) return;
            }
            var welcome = new WelcomeMessage(engine.PeerId, engine.SessionId, ProtocolVersion);
            await MessageFraming.WriteAsync<NetworkMessage>(stream, welcome, timeout.Token);
            var connection = new Connection(client, hello.PeerId, false);
            if (Register(connection)) await RunConnectionAsync(connection);
        }
        catch (Exception exception) when (IsConnectionError(exception))
        {
            if (!lifetime.IsCancellationRequested) Error?.Invoke(exception.Message);
        }
        finally
        {
            client.Dispose();
        }
    }

    private bool Register(Connection connection, (string Host, int Port)? target = null, Guid expectedPeer = default)
    {
        lock (gate)
        {
            if (lifetime.IsCancellationRequested || paused) return false;
            if (target is { } endpoint)
            {
                if (expectedPeer != Guid.Empty && reconnectTargets.GetValueOrDefault(endpoint) != expectedPeer) return false;
                reconnectTargets[endpoint] = connection.PeerId;
            }
            if (connections.TryGetValue(connection.PeerId, out var existing))
            {
                // Both ends keep the same direction when they connect simultaneously.
                bool preferOutgoing = engine.PeerId.CompareTo(connection.PeerId) < 0;
                if (existing.Outgoing == preferOutgoing || connection.Outgoing != preferOutgoing) return false;
                existing.Client.Dispose();
            }
            connections[connection.PeerId] = connection;
        }
        Changed?.Invoke();
        return true;
    }

    private async Task RunConnectionAsync(Connection connection)
    {
        var reading = ReadAsync(connection);
        connection.RepairRequested = true;
        await SendAsync(connection, CreateSyncRequest(), lifetime.Token);
        await reading;
    }

    private SyncRequestMessage CreateSyncRequest()
    {
        var knownIds = engine.GetOperations().Select(operation => operation.Stamp.OperationId).ToImmutableArray();
        return new SyncRequestMessage(knownIds);
    }

    private async Task RespondToSyncAsync(Connection connection, SyncRequestMessage request)
    {
        if (request.KnownOperationIds.IsEmpty)
        {
            var snapshot = engine.CreateSnapshot();
            await SendAsync(connection, new SnapshotMessage(snapshot), lifetime.Token);
            var snapshotOperationIds = snapshot.Operations.Select(operation => operation.Stamp.OperationId);
            var missingOperations = engine.GetMissingOperations(snapshotOperationIds);
            foreach (var operation in missingOperations)
                await SendAsync(connection, new OperationMessage(operation), lifetime.Token);
        }
        else
        {
            foreach (var operation in engine.GetMissingOperations(request.KnownOperationIds))
                await SendAsync(connection, new OperationMessage(operation), lifetime.Token);
        }
        await SendAsync(connection, new SyncCompleteMessage(), lifetime.Token);
    }

    private async Task ReadAsync(Connection connection)
    {
        try
        {
            while (true)
            {
                var message = await MessageFraming.ReadAsync<NetworkMessage>(connection.Client.GetStream(), lifetime.Token);
                if (message is null) break;
                switch (message)
                {
                    case OperationMessage { Operation: not null } received:
                        if (engine.Receive(received.Operation)) Changed?.Invoke();
                        if (!connection.RepairRequested && !engine.GetPendingStrokeIds().IsEmpty)
                        {
                            connection.RepairRequested = true;
                            Track(SendAsync(connection, CreateSyncRequest(), lifetime.Token));
                        }
                        break;
                    case SyncRequestMessage request when !request.KnownOperationIds.IsDefault && request.KnownOperationIds.All(id => id != Guid.Empty):
                        // Send in parallel with reading so two large snapshots cannot block each other.
                        Track(RespondToSyncAsync(connection, request));
                        break;
                    case SnapshotMessage { Snapshot: not null } received:
                        engine.MergeSnapshot(received.Snapshot);
                        Changed?.Invoke();
                        break;
                    case SyncCompleteMessage:
                        connection.RepairRequested = false;
                        break;
                    default:
                        throw new InvalidDataException("Unexpected or invalid message.");
                }
            }
        }
        catch (Exception exception) when (IsConnectionError(exception))
        {
            if (!lifetime.IsCancellationRequested) Error?.Invoke(exception.Message);
        }
        finally
        {
            connection.Client.Dispose();
            lock (gate)
            {
                if (connections.GetValueOrDefault(connection.PeerId) == connection) connections.Remove(connection.PeerId);
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
            if (!lifetime.IsCancellationRequested) Error?.Invoke(exception.Message);
        }
    }

    private void ValidateHello(Guid peerId, Guid sessionId, int version)
    {
        if (peerId == Guid.Empty || peerId == engine.PeerId) throw new InvalidDataException("Invalid or duplicate PeerId.");
        if (sessionId != engine.SessionId) throw new InvalidDataException("SessionId does not match.");
        if (version != ProtocolVersion) throw new InvalidDataException("Protocol version does not match.");
    }

    private async Task ReconnectAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(lifetime.Token))
            {
                var targets = new List<KeyValuePair<(string Host, int Port), Guid>>();
                lock (gate)
                {
                    foreach (var target in reconnectTargets)
                    {
                        if (!connections.ContainsKey(target.Value)) targets.Add(target);
                    }
                }
                foreach (var target in targets)
                {
                    var endpoint = target.Key;
                    Guid expectedPeer = target.Value;
                    try
                    {
                        await ConnectCoreAsync(endpoint.Host, endpoint.Port, expectedPeer, lifetime.Token);
                    }
                    catch (Exception exception) when (IsConnectionError(exception)) { }
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }

    private void Track(Task task)
    {
        lock (gate) tasks.Add(task);
        _ = task.ContinueWith(completed =>
        {
            lock (gate) tasks.Remove(completed);
        }, TaskScheduler.Default);
    }

    private static bool IsConnectionError(Exception exception) => exception is IOException or SocketException or OperationCanceledException or ObjectDisposedException or ArgumentException;

    public async ValueTask DisposeAsync()
    {
        await lifetime.CancelAsync();
        listener?.Stop();
        DisconnectAll();
        if (acceptTask is not null) await acceptTask;
        await reconnectTask;
        Task[] pending;
        lock (gate) pending = tasks.ToArray();
        await Task.WhenAll(pending);
    }

    private sealed class Connection(TcpClient client, Guid peerId, bool outgoing)
    {
        private readonly SemaphoreSlim sendGate = new(1, 1);
        public TcpClient Client { get; } = client;
        public Guid PeerId { get; } = peerId;
        public bool Outgoing { get; } = outgoing;
        public bool RepairRequested { get; set; }

        public async Task SendAsync(NetworkMessage message, CancellationToken cancellationToken)
        {
            await sendGate.WaitAsync(cancellationToken);
            try { await MessageFraming.WriteAsync(Client.GetStream(), message, cancellationToken); }
            finally { sendGate.Release(); }
        }
    }
}
