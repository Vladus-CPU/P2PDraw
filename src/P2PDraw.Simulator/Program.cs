using System.Globalization;
using System.Net.Sockets;
using System.Text.Json;
using P2PDraw.Core.Models;
using P2PDraw.Core.Sync;
using P2PDraw.Networking.Transport;

if (args.Length < 2 || args.Length > 3)
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
    WriteJson(new { @event = "ready", port = peer.Port, peerId = engine.PeerId, sessionId = engine.SessionId });

    while (true)
    {
        string? line = await Console.In.ReadLineAsync(stop.Token);
        if (line is null)
        {
            break;
        }

        string[] commandParts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (commandParts.Length == 0)
        {
            continue;
        }

        try
        {
            switch (commandParts[0].ToLowerInvariant())
            {
                case "connect" when commandParts.Length == 3:
                    int remotePort = int.Parse(commandParts[2], CultureInfo.InvariantCulture);
                    await peer.ConnectAsync(commandParts[1], remotePort, stop.Token);
                    break;
                case "begin" when commandParts.Length == 1:
                    var begin = engine.BeginStroke(0xFF112233, 2);
                    await peer.SendOperationAsync(begin, stop.Token);
                    WriteJson(new { @event = "done", command = "begin", strokeId = begin.StrokeId });
                    continue;
                case "append" when commandParts.Length >= 4 && commandParts.Length % 2 == 0:
                    Guid strokeId = Guid.Parse(commandParts[1]);
                    var points = new List<StrokePoint>();
                    for (int i = 2; i < commandParts.Length; i += 2)
                    {
                        double x = ParseNumber(commandParts[i]);
                        double y = ParseNumber(commandParts[i + 1]);
                        points.Add(new StrokePoint(x, y));
                    }
                    foreach (var append in engine.AppendStrokeBatches(strokeId, points))
                    {
                        await peer.SendOperationAsync(append, stop.Token);
                    }
                    break;
                case "end" when commandParts.Length == 2:
                    var end = engine.EndStroke(Guid.Parse(commandParts[1]));
                    await peer.SendOperationAsync(end, stop.Token);
                    break;
                case "draw" when commandParts.Length == 5:
                    var stroke = engine.BeginStroke(0xFF112233, 2);
                    await peer.SendOperationAsync(stroke, stop.Token);
                    var startPoint = new StrokePoint(ParseNumber(commandParts[1]), ParseNumber(commandParts[2]));
                    var endPoint = new StrokePoint(ParseNumber(commandParts[3]), ParseNumber(commandParts[4]));
                    var strokeAppend = engine.AppendStroke(stroke.StrokeId, [startPoint, endPoint]);
                    await peer.SendOperationAsync(strokeAppend, stop.Token);
                    var strokeEnd = engine.EndStroke(stroke.StrokeId);
                    await peer.SendOperationAsync(strokeEnd, stop.Token);
                    WriteJson(new { @event = "done", command = "draw", strokeId = stroke.StrokeId });
                    continue;
                case "shape" when commandParts.Length == 6:
                    if (!Enum.TryParse<ShapeKind>(commandParts[1], true, out var kind) || !Enum.IsDefined(kind))
                    {
                        throw new ArgumentException("Shape must be line, rectangle or ellipse.");
                    }
                    var shapeStart = new StrokePoint(ParseNumber(commandParts[2]), ParseNumber(commandParts[3]));
                    var shapeEnd = new StrokePoint(ParseNumber(commandParts[4]), ParseNumber(commandParts[5]));
                    var shape = engine.AddShape(kind, shapeStart, shapeEnd, 0xFF112233, 2);
                    await peer.SendOperationAsync(shape, stop.Token);
                    WriteJson(new { @event = "done", command = "shape", objectId = shape.ShapeId });
                    continue;
                case "move" when commandParts.Length == 4:
                    var offset = new StrokePoint(ParseNumber(commandParts[2]), ParseNumber(commandParts[3]));
                    var move = engine.MoveObject(Guid.Parse(commandParts[1]), offset);
                    await peer.SendOperationAsync(move, stop.Token);
                    break;
                case "remove" when commandParts.Length == 2:
                    var remove = engine.RemoveObject(Guid.Parse(commandParts[1]));
                    await peer.SendOperationAsync(remove, stop.Token);
                    break;
                case "clear" when commandParts.Length == 1:
                    var clear = engine.ClearCanvas();
                    await peer.SendOperationAsync(clear, stop.Token);
                    break;
                case "duplicate" when commandParts.Length == 1:
                    foreach (var operation in engine.GetOperations())
                    {
                        await peer.SendOperationAsync(operation, stop.Token);
                    }

                    break;
                case "disconnect" when commandParts.Length == 1:
                    peer.DisconnectAll();
                    break;
                case "drop" when commandParts.Length == 1:
                    peer.DisconnectAll(reconnect: true);
                    break;
                case "state" when commandParts.Length == 1:
                    var snapshot = engine.CreateSnapshot();
                    var operationIds = snapshot.Operations.Select(operation => operation.Stamp.OperationId);
                    WriteJson(new
                    {
                        @event = "state",
                        connections = peer.ConnectionCount,
                        strokes = snapshot.Strokes,
                        shapes = snapshot.Shapes,
                        tombstones = snapshot.Tombstones,
                        pending = snapshot.PendingStrokeIds,
                        operations = operationIds
                    });
                    continue;
                case "snapshot" when commandParts.Length == 1:
                    WriteJson(new { @event = "snapshot", snapshot = engine.CreateSnapshot() });
                    continue;
                case "log" when commandParts.Length == 1:
                    WriteJson(new { @event = "log", operations = engine.GetOperations() });
                    continue;
                case "quit" when commandParts.Length == 1:
                    return 0;
                case "help":
                    string[] commands =
                    [
                        "connect <ip> <port>", "begin", "append <stroke-id> <x> <y> [x y ...]", "end <stroke-id>",
                        "draw <x1> <y1> <x2> <y2>", "shape <line|rectangle|ellipse> <x1> <y1> <x2> <y2>",
                        "move <object-id> <offset-x> <offset-y>", "remove <object-id>", "clear", "duplicate",
                        "disconnect", "drop", "state", "snapshot", "log", "quit"
                    ];
                    WriteJson(new { @event = "help", commands });
                    continue;
                default:
                    throw new ArgumentException("Unknown command or wrong arguments. Use help.");
            }
            WriteJson(new { @event = "done", command = commandParts[0] });
        }
        catch (Exception exception) when (exception is ArgumentException || exception is InvalidOperationException
            || exception is IOException || exception is InvalidDataException || exception is SocketException || exception is OperationCanceledException
            || exception is FormatException || exception is OverflowException)
        {
            WriteJson(new { @event = "error", message = exception.Message });
        }
    }
    return 0;
}
catch (OperationCanceledException)
{
    return 0;
}
catch (Exception exception) when (exception is ArgumentException || exception is SocketException
    || exception is FormatException || exception is OverflowException)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}

static void WriteJson<T>(T value) => Console.WriteLine(JsonSerializer.Serialize(value));
static double ParseNumber(string value) => double.Parse(value, CultureInfo.InvariantCulture);
