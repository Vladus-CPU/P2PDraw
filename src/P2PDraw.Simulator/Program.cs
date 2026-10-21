using System.Globalization;
using System.Net.Sockets;
using System.Text.Json;
using P2PDraw.Core;
using P2PDraw.Networking;

if (args.Length is < 2 or > 3)
{
    Console.WriteLine("Usage: P2PDraw.Simulator <port> <session-guid> [peer-guid]");
    return 1;
}

try
{
    int port = int.Parse(args[0], CultureInfo.InvariantCulture);
    var engine = new SyncEngine(args.Length == 3 ? Guid.Parse(args[2]) : Guid.NewGuid(), Guid.Parse(args[1]));
    await using var peer = new TcpPeer(engine);
    using var stop = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
    peer.Error += error => Console.Error.WriteLine(error);
    peer.Start(port);
    Write(new { @event = "ready", port = peer.Port, peerId = engine.PeerId, sessionId = engine.SessionId });

    while (await Console.In.ReadLineAsync(stop.Token) is { } line)
    {
        string[] words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0) continue;
        try
        {
            switch (words[0].ToLowerInvariant())
            {
                case "connect" when words.Length == 3:
                    await peer.ConnectAsync(words[1], int.Parse(words[2], CultureInfo.InvariantCulture), stop.Token);
                    break;
                case "begin" when words.Length == 1:
                    var begin = engine.BeginStroke(0xFF112233, 2);
                    await peer.SendOperationAsync(begin, stop.Token);
                    Write(new { @event = "done", command = "begin", strokeId = begin.StrokeId });
                    continue;
                case "append" when words.Length >= 4 && words.Length % 2 == 0:
                    var points = Enumerable.Range(0, (words.Length - 2) / 2).Select(i => new StrokePoint(Number(words[2 + 2 * i]), Number(words[3 + 2 * i])));
                    await peer.SendOperationAsync(engine.AppendStroke(Guid.Parse(words[1]), points), stop.Token);
                    break;
                case "end" when words.Length == 2:
                    await peer.SendOperationAsync(engine.EndStroke(Guid.Parse(words[1])), stop.Token);
                    break;
                case "draw" when words.Length == 5:
                    var stroke = engine.BeginStroke(0xFF112233, 2);
                    await peer.SendOperationAsync(stroke, stop.Token);
                    await peer.SendOperationAsync(engine.AppendStroke(stroke.StrokeId, [new(Number(words[1]), Number(words[2])), new(Number(words[3]), Number(words[4]))]), stop.Token);
                    await peer.SendOperationAsync(engine.EndStroke(stroke.StrokeId), stop.Token);
                    Write(new { @event = "done", command = "draw", strokeId = stroke.StrokeId });
                    continue;
                case "remove" when words.Length == 2:
                    await peer.SendOperationAsync(engine.RemoveStroke(Guid.Parse(words[1])), stop.Token);
                    break;
                case "clear" when words.Length == 1:
                    await peer.SendOperationAsync(engine.ClearCanvas(), stop.Token);
                    break;
                case "duplicate" when words.Length == 1:
                    foreach (var operation in engine.GetOperations()) await peer.SendOperationAsync(operation, stop.Token);
                    break;
                case "disconnect" when words.Length == 1:
                    peer.DisconnectAll();
                    break;
                case "state" when words.Length == 1:
                    Write(new { @event = "state", connections = peer.ConnectionCount, strokes = engine.GetStrokes(), tombstones = engine.GetTombstones(),
                        pending = engine.GetPendingStrokeIds(), operations = engine.GetOperations().Select(op => op.Stamp.OperationId) });
                    continue;
                case "log" when words.Length == 1:
                    Write(new { @event = "log", operations = engine.GetOperations() });
                    continue;
                case "quit" when words.Length == 1:
                    return 0;
                case "help":
                    Write(new { @event = "help", commands = new[] { "connect <ip> <port>", "begin", "append <stroke-id> <x> <y> [x y ...]", "end <stroke-id>", "draw <x1> <y1> <x2> <y2>", "remove <stroke-id>", "clear", "duplicate", "disconnect", "state", "log", "quit" } });
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
static double Number(string value) => double.Parse(value, CultureInfo.InvariantCulture);
