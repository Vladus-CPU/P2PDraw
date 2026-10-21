using P2PDraw.Core.Models;
using P2PDraw.Core.Sync;
using P2PDraw.Networking.Transport;
using System.Globalization;
using System.Net.Sockets;
using System.Text.Json;

if (args.Length is < 2 or > 3)
{
    Console.WriteLine("Usage: P2PDraw.Simulator <port> <session-guid> [peer-guid]");
    return 1;
}

try
{
    int port = int.Parse(args[0], CultureInfo.InvariantCulture);
    Guid peerId = args.Length == 3 ? Guid.Parse(args[2]) : Guid.NewGuid();
    Guid sessionId = Guid.Parse(args[1]);
    var engine = new SyncEngine(peerId, sessionId);
    await using var peer = new TcpPeer(engine);
    using var stop = new CancellationTokenSource();
    Console.CancelKeyPress += (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        stop.Cancel();
    };
    peer.Error += error => Console.Error.WriteLine(error);
    peer.Start(port);
    Write(new { @event = "ready", port = peer.Port, peerId = engine.PeerId, sessionId = engine.SessionId });

    while (true)
    {
        string? line = await Console.In.ReadLineAsync(stop.Token);
        if (line is null) break;
        string[] words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0) continue;
        try
        {
            switch (words[0].ToLowerInvariant())
            {
                case "connect" when words.Length == 3:
                    int remotePort = int.Parse(words[2], CultureInfo.InvariantCulture);
                    await peer.ConnectAsync(words[1], remotePort, stop.Token);
                    break;
                case "begin" when words.Length == 1:
                    var begin = engine.BeginStroke(0xFF112233, 2);
                    await peer.SendOperationAsync(begin, stop.Token);
                    Write(new { @event = "done", command = "begin", strokeId = begin.StrokeId });
                    continue;
                case "append" when words.Length >= 4 && words.Length % 2 == 0:
                    Guid strokeId = Guid.Parse(words[1]);
                    var points = new List<StrokePoint>();
                    for (int i = 2; i < words.Length; i += 2)
                    {
                        double x = ParseNumber(words[i]);
                        double y = ParseNumber(words[i + 1]);
                        points.Add(new StrokePoint(x, y));
                    }
                    var append = engine.AppendStroke(strokeId, points);
                    await peer.SendOperationAsync(append, stop.Token);
                    break;
                case "end" when words.Length == 2:
                    var end = engine.EndStroke(Guid.Parse(words[1]));
                    await peer.SendOperationAsync(end, stop.Token);
                    break;
                case "draw" when words.Length == 5:
                    var stroke = engine.BeginStroke(0xFF112233, 2);
                    await peer.SendOperationAsync(stroke, stop.Token);
                    var startPoint = new StrokePoint(ParseNumber(words[1]), ParseNumber(words[2]));
                    var endPoint = new StrokePoint(ParseNumber(words[3]), ParseNumber(words[4]));
                    var strokeAppend = engine.AppendStroke(stroke.StrokeId, [startPoint, endPoint]);
                    await peer.SendOperationAsync(strokeAppend, stop.Token);
                    var strokeEnd = engine.EndStroke(stroke.StrokeId);
                    await peer.SendOperationAsync(strokeEnd, stop.Token);
                    Write(new { @event = "done", command = "draw", strokeId = stroke.StrokeId });
                    continue;
                case "remove" when words.Length == 2:
                    var remove = engine.RemoveStroke(Guid.Parse(words[1]));
                    await peer.SendOperationAsync(remove, stop.Token);
                    break;
                case "clear" when words.Length == 1:
                    var clear = engine.ClearCanvas();
                    await peer.SendOperationAsync(clear, stop.Token);
                    break;
                case "duplicate" when words.Length == 1:
                    foreach (var operation in engine.GetOperations()) await peer.SendOperationAsync(operation, stop.Token);
                    break;
                case "disconnect" when words.Length == 1:
                    peer.DisconnectAll();
                    break;
                case "drop" when words.Length == 1:
                    peer.DisconnectAll(reconnect: true);
                    break;
                case "state" when words.Length == 1:
                    var snapshot = engine.CreateSnapshot();
                    var operationIds = snapshot.Operations.Select(operation => operation.Stamp.OperationId);
                    Write(new { @event = "state", connections = peer.ConnectionCount, strokes = snapshot.Strokes, tombstones = snapshot.Tombstones,
                        pending = snapshot.PendingStrokeIds, operations = operationIds });
                    continue;
                case "snapshot" when words.Length == 1:
                    Write(new { @event = "snapshot", snapshot = engine.CreateSnapshot() });
                    continue;
                case "log" when words.Length == 1:
                    Write(new { @event = "log", operations = engine.GetOperations() });
                    continue;
                case "quit" when words.Length == 1:
                    return 0;
                case "help":
                    string[] commands = ["connect <ip> <port>", "begin", "append <stroke-id> <x> <y> [x y ...]", "end <stroke-id>",
                        "draw <x1> <y1> <x2> <y2>", "remove <stroke-id>", "clear", "duplicate", "disconnect", "drop", "state", "snapshot", "log", "quit"];
                    Write(new { @event = "help", commands });
                    continue;
                default:
                    throw new ArgumentException("Unknown command or wrong arguments. Use help.");
            }
            Write(new { @event = "done", command = words[0] });
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IOException or SocketException or OperationCanceledException or FormatException or OverflowException)
        {
            Write(new { @event = "error", message = exception.Message });
        }
    }
    return 0;
}
catch (OperationCanceledException) { return 0; }
catch (Exception exception) when (exception is ArgumentException or SocketException or FormatException or OverflowException)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}

static void Write<T>(T value) => Console.WriteLine(JsonSerializer.Serialize(value));
static double ParseNumber(string value) => double.Parse(value, CultureInfo.InvariantCulture);
