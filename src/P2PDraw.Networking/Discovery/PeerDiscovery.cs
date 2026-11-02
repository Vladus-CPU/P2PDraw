using System.Collections.Immutable;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using P2PDraw.Networking.Transport;

namespace P2PDraw.Networking.Discovery;

public sealed record DiscoveryAnnouncement(Guid PeerId, Guid SessionId, int TcpPort, int ProtocolVersion, string SessionName = "");
public sealed record DiscoveredPeer(Guid PeerId, string Address, int TcpPort, DateTimeOffset LastSeen, Guid SessionId = default, string SessionName = "")
{
    public string DisplayName => string.IsNullOrWhiteSpace(SessionName) ? $"Сесія {SessionId.ToString()[..8]}" : SessionName;
}

public sealed class PeerDiscovery : IAsyncDisposable
{
    public const int DefaultPort = 5100;
    public static readonly IPAddress MulticastAddress = IPAddress.Parse("239.255.51.10");
    private readonly object stateLock = new();
    private readonly Guid peerId;
    private readonly Guid sessionId;
    private readonly bool includeOtherSessions;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Dictionary<Guid, DiscoveredPeer> peers = [];
    private readonly List<IPAddress> interfaces = [];
    private UdpClient? client;
    private Task? receivingTask;
    private Task? announcingTask;

    public event Action? Changed;
    public event Action<string>? Error;
    public int Port => (client?.Client.LocalEndPoint as IPEndPoint)?.Port ?? 0;

    public PeerDiscovery(Guid peerId, Guid sessionId, bool includeOtherSessions = false)
    {
        if (peerId == Guid.Empty || sessionId == Guid.Empty)
        {
            throw new ArgumentException("Peer and session IDs are required.");
        }
        this.peerId = peerId;
        this.sessionId = sessionId;
        this.includeOtherSessions = includeOtherSessions;
    }

    public ImmutableArray<DiscoveredPeer> GetPeers()
    {
        lock (stateLock)
        {
            return peers.Values.OrderBy(peer => peer.PeerId).ToImmutableArray();
        }
    }

    public void Start(int tcpPort, int udpPort = DefaultPort, IReadOnlyList<IPAddress>? localAddresses = null)
    {
        if (tcpPort < 1 || tcpPort > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(tcpPort));
        }
        StartCore(tcpPort, udpPort, localAddresses);
    }

    public void StartBrowsing(int udpPort = DefaultPort, IReadOnlyList<IPAddress>? localAddresses = null)
    {
        StartCore(0, udpPort, localAddresses);
    }

    private void StartCore(int tcpPort, int udpPort, IReadOnlyList<IPAddress>? localAddresses)
    {
        ObjectDisposedException.ThrowIf(lifetime.IsCancellationRequested, this);
        if (client is not null)
        {
            throw new InvalidOperationException("Discovery already started.");
        }
        var addresses = localAddresses ?? GetLocalAddresses();
        var socket = new UdpClient(AddressFamily.InterNetwork);
        try
        {
            socket.ExclusiveAddressUse = false;
            socket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.Client.Bind(new IPEndPoint(IPAddress.Any, udpPort));
            socket.MulticastLoopback = true;
            socket.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 1);
            foreach (var address in addresses.Distinct())
            {
                try
                {
                    socket.JoinMulticastGroup(MulticastAddress, address);
                    interfaces.Add(address);
                }
                catch (SocketException exception)
                {
                    Error?.Invoke($"{address}: {exception.Message}");
                }
            }
            if (interfaces.Count == 0)
            {
                throw new IOException("No IPv4 multicast interface is available. Use manual TCP connection.");
            }
        }
        catch
        {
            interfaces.Clear();
            socket.Dispose();
            throw;
        }
        client = socket;
        receivingTask = ReceiveAsync();
        announcingTask = AnnounceAsync(tcpPort);
    }

    private static List<IPAddress> GetLocalAddresses()
    {
        var addresses = new List<IPAddress>();
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up || !adapter.SupportsMulticast)
            {
                continue;
            }
            foreach (var unicast in adapter.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily == AddressFamily.InterNetwork)
                {
                    addresses.Add(unicast.Address);
                }
            }
        }
        if (addresses.Count == 0)
        {
            addresses.Add(IPAddress.Loopback);
        }
        return addresses;
    }

    private async Task ReceiveAsync()
    {
        try
        {
            while (true)
            {
                var datagram = await client!.ReceiveAsync(lifetime.Token);
                if (datagram.Buffer.Length > 1024)
                {
                    continue;
                }
                DiscoveryAnnouncement? announcement;
                try
                {
                    announcement = JsonSerializer.Deserialize<DiscoveryAnnouncement>(datagram.Buffer);
                }
                catch (JsonException)
                {
                    continue;
                }
                if (announcement is null || announcement.PeerId == Guid.Empty || announcement.PeerId == peerId
                    || announcement.SessionId == Guid.Empty || (!includeOtherSessions && announcement.SessionId != sessionId)
                    || announcement.ProtocolVersion != TcpPeer.ProtocolVersion
                    || announcement.TcpPort < 1 || announcement.TcpPort > 65535)
                {
                    continue;
                }
                // Use the datagram source instead of trusting an advertised IP address.
                string name = announcement.SessionName ?? "";
                if (name.Length > 64 || name.Any(char.IsControl))
                {
                    continue;
                }
                var found = new DiscoveredPeer(announcement.PeerId, datagram.RemoteEndPoint.Address.ToString(), announcement.TcpPort,
                    DateTimeOffset.UtcNow, announcement.SessionId, name);
                lock (stateLock)
                {
                    peers[found.PeerId] = found;
                }
                Changed?.Invoke();
            }
        }
        catch (Exception exception) when (exception is SocketException || exception is OperationCanceledException || exception is ObjectDisposedException)
        {
            if (!lifetime.IsCancellationRequested)
            {
                Error?.Invoke(exception.Message);
            }
        }
    }

    private async Task AnnounceAsync(int tcpPort)
    {
        var announcement = new DiscoveryAnnouncement(peerId, sessionId, tcpPort, TcpPeer.ProtocolVersion, $"Сесія {sessionId.ToString()[..8]}");
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(announcement);
        var destination = new IPEndPoint(MulticastAddress, Port);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        try
        {
            do
            {
                foreach (var address in interfaces)
                {
                    if (tcpPort == 0)
                    {
                        break;
                    }
                    try
                    {
                        client!.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, address.GetAddressBytes());
                        await client.SendAsync(payload, destination, lifetime.Token);
                    }
                    catch (SocketException exception)
                    {
                        Error?.Invoke($"{address}: {exception.Message}");
                    }
                }
                bool expired = false;
                lock (stateLock)
                {
                    var threshold = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(8);
                    foreach (var found in peers.Values.ToArray())
                    {
                        if (found.LastSeen < threshold)
                        {
                            peers.Remove(found.PeerId);
                            expired = true;
                        }
                    }
                }
                if (expired)
                {
                    Changed?.Invoke();
                }
            }
            while (await timer.WaitForNextTickAsync(lifetime.Token));
        }
        catch (Exception exception) when (exception is OperationCanceledException || exception is ObjectDisposedException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        await lifetime.CancelAsync();
        client?.Dispose();
        if (receivingTask is not null)
        {
            await receivingTask;
        }
        if (announcingTask is not null)
        {
            await announcingTask;
        }
    }
}
