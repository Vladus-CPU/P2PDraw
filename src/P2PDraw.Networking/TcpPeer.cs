using System.Net;
using System.Net.Sockets;
using P2PDraw.Core;

namespace P2PDraw.Networking;

public sealed class TcpPeer : IAsyncDisposable
{
    public const int ProtocolVersion = 1;
    private readonly object gate = new();
    private readonly SyncEngine engine;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Dictionary<Guid, Connection> connections = [];
    private readonly HashSet<Task> tasks = [];
    private TcpListener? listener;
    private Task? acceptTask;

    public event Action? Changed;
    public event Action<string>? Error;
    public int Port => (listener?.LocalEndpoint as IPEndPoint)?.Port ?? 0;
    public int ConnectionCount { get { lock (gate) return connections.Count; } }

    public TcpPeer(SyncEngine engine) => this.engine = engine;

    public void Start(int port, IPAddress? address = null)
    {
        ObjectDisposedException.ThrowIf(lifetime.IsCancellationRequested, this);
        if (listener is not null) throw new InvalidOperationException("Listener already started.");
        listener = new TcpListener(address ?? IPAddress.Any, port);
        listener.Start();
        acceptTask = AcceptAsync();
    }

    public async Task<Guid> ConnectAsync(string host, int port, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var client = new TcpClient { NoDelay = true };
        try
        {
            await client.ConnectAsync(host, port, timeout.Token);
            var stream = client.GetStream();
            await MessageFraming.WriteAsync<NetworkMessage>(stream, new HelloMessage(engine.PeerId, engine.SessionId, ProtocolVersion), timeout.Token);
            if (await MessageFraming.ReadAsync<NetworkMessage>(stream, timeout.Token) is not WelcomeMessage welcome)
                throw new InvalidDataException("Expected WELCOME.");
            ValidateHello(welcome.PeerId, welcome.SessionId, welcome.ProtocolVersion);
            var connection = new Connection(client, welcome.PeerId, true);
            if (Register(connection)) Track(ReadAsync(connection));
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
        await Task.WhenAll(targets.Select(connection => SendAsync(connection, new OperationMessage(operation), cancellationToken)));
    }

    public void DisconnectAll()
    {
        lock (gate)
        {
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
            if (await MessageFraming.ReadAsync<NetworkMessage>(stream, timeout.Token) is not HelloMessage hello)
                throw new InvalidDataException("Expected HELLO.");
            ValidateHello(hello.PeerId, hello.SessionId, hello.ProtocolVersion);
            await MessageFraming.WriteAsync<NetworkMessage>(stream, new WelcomeMessage(engine.PeerId, engine.SessionId, ProtocolVersion), timeout.Token);
            var connection = new Connection(client, hello.PeerId, false);
            if (Register(connection)) await ReadAsync(connection);
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

    private bool Register(Connection connection)
    {
        lock (gate)
        {
            if (lifetime.IsCancellationRequested) return false;
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

    private async Task ReadAsync(Connection connection)
    {
        try
        {
            while (await MessageFraming.ReadAsync<NetworkMessage>(connection.Client.GetStream(), lifetime.Token) is { } message)
            {
                if (message is not OperationMessage { Operation: not null } received) throw new InvalidDataException("Expected operation.");
                if (engine.Receive(received.Operation)) Changed?.Invoke();
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

    private void Track(Task task)
    {
        lock (gate) tasks.Add(task);
        _ = task.ContinueWith(completed => { lock (gate) tasks.Remove(completed); }, TaskScheduler.Default);
    }

    private static bool IsConnectionError(Exception exception) => exception is IOException or SocketException or OperationCanceledException or ObjectDisposedException or ArgumentException;

    public async ValueTask DisposeAsync()
    {
        await lifetime.CancelAsync();
        listener?.Stop();
        DisconnectAll();
        if (acceptTask is not null) await acceptTask;
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

        public async Task SendAsync(NetworkMessage message, CancellationToken cancellationToken)
        {
            await sendGate.WaitAsync(cancellationToken);
            try { await MessageFraming.WriteAsync(Client.GetStream(), message, cancellationToken); }
            finally { sendGate.Release(); }
        }
    }
}
