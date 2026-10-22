using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Xunit;

namespace P2PDraw.Tests.Integration;

public class SimulatorProcessTests
{
    [Fact]
    public async Task ConcurrentDrawingRemovalAndDuplicatesConverge()
    {
        var session = Guid.NewGuid();
        await using var firstProcess = new SimulatorProcess(session);
        await using var secondProcess = new SimulatorProcess(session);
        await ConnectProcessesAsync(firstProcess, secondProcess);

        var firstDrawing = firstProcess.CommandAsync("draw 1 2 3 4");
        var secondDrawing = secondProcess.CommandAsync("draw 5 6 7 8");
        var drawings = await Task.WhenAll(firstDrawing, secondDrawing);
        await WaitForStateAsync(firstProcess, secondProcess, 6, 1);
        await firstProcess.CommandAsync("duplicate");
        Guid removedStrokeId = drawings[0].GetProperty("strokeId").GetGuid();
        await secondProcess.CommandAsync($"remove {removedStrokeId}");
        var states = await WaitForStateAsync(firstProcess, secondProcess, 7, 1);

        Assert.Single(states[0].GetProperty("strokes").EnumerateArray());
        Assert.Single(states[0].GetProperty("tombstones").EnumerateArray());
        Assert.Equal(states[0].GetRawText(), states[1].GetRawText());
    }

    [Fact]
    public async Task OfflineClearAndDrawingConvergeAfterReconnect()
    {
        var session = Guid.NewGuid();
        await using var firstProcess = new SimulatorProcess(session);
        await using var secondProcess = new SimulatorProcess(session);
        int remotePort = await ConnectProcessesAsync(firstProcess, secondProcess);
        await firstProcess.CommandAsync("draw 1 2 3 4");
        await WaitForStateAsync(firstProcess, secondProcess, 3, 1);

        await firstProcess.CommandAsync("disconnect");
        await WaitForStateAsync(firstProcess, secondProcess, 3, 0);
        var clearing = firstProcess.CommandAsync("clear");
        var drawing = secondProcess.CommandAsync("draw 9 10 11 12");
        await Task.WhenAll(clearing, drawing);
        await firstProcess.CommandAsync($"connect 127.0.0.1 {remotePort}");
        var states = await WaitForStateAsync(firstProcess, secondProcess, 7, 1);

        Assert.Equal(states[0].GetRawText(), states[1].GetRawText());
        Assert.Single(states[0].GetProperty("strokes").EnumerateArray());
        Assert.Single(states[0].GetProperty("tombstones").EnumerateArray());
    }

    [Fact]
    public async Task DroppedConnectionRecoversConcurrentDrawingAndRemoval()
    {
        var session = Guid.NewGuid();
        await using var firstProcess = new SimulatorProcess(session);
        await using var secondProcess = new SimulatorProcess(session);
        await ConnectProcessesAsync(firstProcess, secondProcess);
        var existingDrawing = await secondProcess.CommandAsync("draw 1 2 3 4");
        Guid existingStrokeId = existingDrawing.GetProperty("strokeId").GetGuid();
        await WaitForStateAsync(firstProcess, secondProcess, 3, 1);

        await firstProcess.CommandAsync("drop");
        var drawing = firstProcess.CommandAsync("draw 13 14 15 16");
        var removal = secondProcess.CommandAsync($"remove {existingStrokeId}");
        await Task.WhenAll(drawing, removal);
        var states = await WaitForStateAsync(firstProcess, secondProcess, 7, 1);

        Assert.Equal(states[0].GetRawText(), states[1].GetRawText());
        Assert.Single(states[0].GetProperty("strokes").EnumerateArray());
        Assert.Single(states[0].GetProperty("tombstones").EnumerateArray());
    }

    private static async Task<int> ConnectProcessesAsync(SimulatorProcess firstProcess, SimulatorProcess secondProcess)
    {
        var firstReady = await firstProcess.ReadAsync("ready");
        var secondReady = await secondProcess.ReadAsync("ready");
        Guid firstPeerId = firstReady.GetProperty("peerId").GetGuid();
        Guid secondPeerId = secondReady.GetProperty("peerId").GetGuid();
        int firstPort = firstReady.GetProperty("port").GetInt32();
        int secondPort = secondReady.GetProperty("port").GetInt32();
        Assert.NotEqual(firstPeerId, secondPeerId);
        Assert.NotEqual(firstPort, secondPort);

        await firstProcess.CommandAsync($"connect 127.0.0.1 {secondPort}");
        await WaitForStateAsync(firstProcess, secondProcess, 0, 1);
        return secondPort;
    }

    private static async Task<JsonElement[]> WaitForStateAsync(SimulatorProcess firstProcess, SimulatorProcess secondProcess, int operationCount, int connections)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (true)
        {
            var states = await Task.WhenAll(firstProcess.CommandAsync("state"), secondProcess.CommandAsync("state"));
            bool bothReady = true;
            foreach (var state in states)
            {
                int receivedOperations = state.GetProperty("operations").GetArrayLength();
                int activeConnections = state.GetProperty("connections").GetInt32();
                if (receivedOperations != operationCount || activeConnections != connections) bothReady = false;
            }
            if (bothReady) return states;
            await Task.Delay(20, timeout.Token);
        }
    }

    private sealed class SimulatorProcess : IAsyncDisposable
    {
        private readonly Process process;
        private readonly Task<string> errors;

        public SimulatorProcess(Guid session)
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root is not null && !File.Exists(Path.Combine(root.FullName, "P2PDraw.sln"))) root = root.Parent;
            if (root is null) throw new InvalidOperationException("Solution directory not found.");
            string configuration = typeof(SimulatorProcessTests).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()!.Configuration;
            string assembly = Path.Combine(root.FullName, "src", "P2PDraw.Simulator", "bin", configuration, "net10.0", "P2PDraw.Simulator.dll");
            var start = new ProcessStartInfo("dotnet") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            start.ArgumentList.Add(assembly);
            start.ArgumentList.Add("0");
            start.ArgumentList.Add(session.ToString());
            process = Process.Start(start) ?? throw new InvalidOperationException("Simulator did not start.");
            errors = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        }

        public async Task<JsonElement> CommandAsync(string command)
        {
            await process.StandardInput.WriteLineAsync(command.AsMemory(), TestContext.Current.CancellationToken);
            return await ReadAsync(command is "state" or "log" ? command : "done");
        }

        public async Task<JsonElement> ReadAsync(string eventName)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            string? line = await process.StandardOutput.ReadLineAsync(timeout.Token);
            if (line is null) throw new InvalidOperationException($"Simulator exited: {await errors}");
            using var json = JsonDocument.Parse(line);
            Assert.Equal(eventName, json.RootElement.GetProperty("event").GetString());
            return json.RootElement.Clone();
        }

        public async ValueTask DisposeAsync()
        {
            if (!process.HasExited)
            {
                process.StandardInput.Close();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try
                {
                    await process.WaitForExitAsync(timeout.Token);
                }
                catch (OperationCanceledException)
                {
                    process.Kill(true);
                    await process.WaitForExitAsync(TestContext.Current.CancellationToken);
                }
            }
            Assert.Equal(0, process.ExitCode);
            process.Dispose();
        }
    }
}
