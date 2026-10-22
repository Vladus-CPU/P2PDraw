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

        var firstDrawingTask = firstProcess.CommandAsync("draw 1 2 3 4");
        var secondDrawingTask = secondProcess.CommandAsync("draw 5 6 7 8");
        var drawings = await Task.WhenAll(firstDrawingTask, secondDrawingTask);
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
        var clearingTask = firstProcess.CommandAsync("clear");
        var drawingTask = secondProcess.CommandAsync("draw 9 10 11 12");
        await Task.WhenAll(clearingTask, drawingTask);
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
        var drawingTask = firstProcess.CommandAsync("draw 13 14 15 16");
        var removalTask = secondProcess.CommandAsync($"remove {existingStrokeId}");
        await Task.WhenAll(drawingTask, removalTask);
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

    private static async Task<JsonElement[]> WaitForStateAsync(SimulatorProcess firstProcess, SimulatorProcess secondProcess,
        int operationCount, int connections)
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
                if (receivedOperations != operationCount || activeConnections != connections)
                {
                    bothReady = false;
                }
            }
            if (bothReady)
            {
                return states;
            }

            await Task.Delay(20, timeout.Token);
        }
    }

    private sealed class SimulatorProcess : IAsyncDisposable
    {
        private readonly Process process;
        private readonly Task<string> standardErrors;

        public SimulatorProcess(Guid session)
        {
            var solutionDirectory = new DirectoryInfo(AppContext.BaseDirectory);
            while (solutionDirectory is not null && !File.Exists(Path.Combine(solutionDirectory.FullName, "P2PDraw.sln")))
            {
                solutionDirectory = solutionDirectory.Parent;
            }

            if (solutionDirectory is null)
            {
                throw new InvalidOperationException("Solution directory not found.");
            }

            string configuration = typeof(SimulatorProcessTests).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()!.Configuration;
            string assembly = Path.Combine(solutionDirectory.FullName, "src", "P2PDraw.Simulator", "bin", configuration, "net10.0", "P2PDraw.Simulator.dll");
            var startInfo = new ProcessStartInfo("dotnet")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add(assembly);
            startInfo.ArgumentList.Add("0");
            startInfo.ArgumentList.Add(session.ToString());
            var startedProcess = Process.Start(startInfo);
            if (startedProcess is null)
            {
                throw new InvalidOperationException("Simulator did not start.");
            }
            process = startedProcess;
            standardErrors = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        }

        public async Task<JsonElement> CommandAsync(string command)
        {
            await process.StandardInput.WriteLineAsync(command.AsMemory(), TestContext.Current.CancellationToken);
            string eventName = command == "state" || command == "log" ? command : "done";
            return await ReadAsync(eventName);
        }

        public async Task<JsonElement> ReadAsync(string eventName)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            string? line = await process.StandardOutput.ReadLineAsync(timeout.Token);
            if (line is null)
            {
                throw new InvalidOperationException($"Simulator exited: {await standardErrors}");
            }

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
