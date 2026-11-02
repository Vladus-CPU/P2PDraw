using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Xunit;

namespace P2PDraw.Tests.Integration;

public class SimulatorProcessTests
{
    [Fact]
    public async Task RejectedSessionDoesNotStopSimulator()
    {
        await using var first = new SimulatorProcess(Guid.NewGuid());
        await using var incompatible = new SimulatorProcess(Guid.NewGuid());
        await first.ReadAsync("ready");
        var remoteReady = await incompatible.ReadAsync("ready");
        int remotePort = remoteReady.GetProperty("port").GetInt32();

        await first.CommandAsync($"connect 127.0.0.1 {remotePort}", "error");
        var created = await first.CommandAsync("shape rectangle 10 20 30 40");
        var state = await first.CommandAsync("state");

        Assert.NotEqual(Guid.Empty, created.GetProperty("objectId").GetGuid());
        Assert.Single(state.GetProperty("shapes").EnumerateArray());
        Assert.Equal(0, state.GetProperty("connections").GetInt32());
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public async Task VectorEditsRecoverAcrossIndependentMeshProcesses(int peerCount)
    {
        var session = Guid.NewGuid();
        var processes = new List<SimulatorProcess>();
        var ports = new List<int>();
        try
        {
            for (int index = 0; index < peerCount; index++)
            {
                var process = new SimulatorProcess(session);
                processes.Add(process);
                var ready = await process.ReadAsync("ready");
                ports.Add(ready.GetProperty("port").GetInt32());
            }
            var joining = new List<Task<JsonElement>>();
            for (int index = 1; index < peerCount; index++)
            {
                joining.Add(processes[index].CommandAsync($"connect 127.0.0.1 {ports[0]}"));
            }
            await Task.WhenAll(joining);
            await WaitForMeshAsync(processes, 0, peerCount - 1);

            string[] kinds = ["line", "rectangle", "ellipse"];
            var creating = new List<Task<JsonElement>>();
            for (int index = 0; index < peerCount; index++)
            {
                creating.Add(processes[index].CommandAsync($"shape {kinds[index % 3]} {index} 10 {index + 30} 40"));
            }
            var created = await Task.WhenAll(creating);
            Guid objectId = created[0].GetProperty("objectId").GetGuid();
            await WaitForMeshAsync(processes, peerCount, peerCount - 1);
            var moving = new List<Task<JsonElement>>();
            for (int index = 0; index < peerCount; index++)
            {
                moving.Add(processes[index].CommandAsync($"move {objectId} {index * 10} {index * 20}"));
            }
            await Task.WhenAll(moving);
            var states = await WaitForMeshAsync(processes, peerCount * 2, peerCount - 1);
            foreach (var state in states)
            {
                AssertMatchingDocuments(states[0], state);
                Assert.Equal(peerCount, state.GetProperty("shapes").GetArrayLength());
            }

            await processes[0].DisposeAsync();
            processes.RemoveAt(0);
            ports.RemoveAt(0);
            await WaitForMeshAsync(processes, peerCount * 2, peerCount - 2);
            await processes[0].CommandAsync("disconnect");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            while ((await processes[0].CommandAsync("state")).GetProperty("connections").GetInt32() != 0)
            {
                await Task.Delay(10, timeout.Token);
            }

            await processes[0].CommandAsync($"remove {objectId}");
            await processes[1].CommandAsync($"move {objectId} 70 80");
            await processes[0].CommandAsync("shape rectangle 100 110 150 160");
            var offlineState = await processes[0].CommandAsync("state");
            Assert.Equal(0, offlineState.GetProperty("connections").GetInt32());
            await processes[0].CommandAsync($"connect 127.0.0.1 {ports[1]}");
            states = await WaitForMeshAsync(processes, peerCount * 2 + 3, peerCount - 2);
            foreach (var state in states)
            {
                AssertMatchingDocuments(states[0], state);
                Assert.Single(state.GetProperty("tombstones").EnumerateArray());
                Assert.Equal(peerCount, state.GetProperty("shapes").GetArrayLength());
            }

            var lateProcess = new SimulatorProcess(session);
            processes.Add(lateProcess);
            await lateProcess.ReadAsync("ready");
            await lateProcess.CommandAsync($"connect 127.0.0.1 {ports[0]}");
            states = await WaitForMeshAsync(processes, peerCount * 2 + 3, peerCount - 1);
            foreach (var state in states)
            {
                AssertMatchingDocuments(states[0], state);
            }
        }
        finally
        {
            foreach (var process in processes)
            {
                await process.DisposeAsync();
            }
        }
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public async Task FullMeshContinuesAfterFirstProcessExits(int peerCount)
    {
        var session = Guid.NewGuid();
        var processes = new List<SimulatorProcess>();
        var ports = new List<int>();
        try
        {
            for (int index = 0; index < peerCount; index++)
            {
                var process = new SimulatorProcess(session);
                processes.Add(process);
                var ready = await process.ReadAsync("ready");
                ports.Add(ready.GetProperty("port").GetInt32());
            }
            Assert.Equal(peerCount, ports.Distinct().Count());

            // Both directions race for the first pair; the mesh must keep one socket per pair.
            await Task.WhenAll(processes[0].CommandAsync($"connect 127.0.0.1 {ports[1]}"),
                processes[1].CommandAsync($"connect 127.0.0.1 {ports[0]}"));
            for (int first = 0; first < peerCount; first++)
            {
                for (int second = first + 1; second < peerCount; second++)
                {
                    await processes[first].CommandAsync($"connect 127.0.0.1 {ports[second]}");
                }
            }
            await WaitForMeshAsync(processes, 0, peerCount - 1);

            var drawingTasks = new List<Task<JsonElement>>();
            for (int index = 0; index < peerCount; index++)
            {
                drawingTasks.Add(processes[index].CommandAsync($"draw {index} 1 {index + 1} 2"));
            }
            await Task.WhenAll(drawingTasks);
            var states = await WaitForMeshAsync(processes, peerCount * 3, peerCount - 1);
            foreach (var state in states)
            {
                AssertMatchingDocuments(states[0], state);
            }

            var departing = processes[0];
            await departing.DisposeAsync();
            processes.RemoveAt(0);
            await WaitForMeshAsync(processes, peerCount * 3, peerCount - 2);
            await Task.WhenAll(processes[0].CommandAsync("clear"), processes[1].CommandAsync("draw 20 21 22 23"));
            states = await WaitForMeshAsync(processes, peerCount * 3 + 4, peerCount - 2);
            foreach (var state in states)
            {
                AssertMatchingDocuments(states[0], state);
                int removedCount = state.GetProperty("tombstones").GetArrayLength();
                int visibleCount = state.GetProperty("strokes").GetArrayLength();
                Assert.InRange(removedCount, peerCount, peerCount + 1);
                Assert.Equal(peerCount + 1, removedCount + visibleCount);
            }
        }
        finally
        {
            foreach (var process in processes)
            {
                await process.DisposeAsync();
            }
        }
    }

    private static void AssertMatchingDocuments(JsonElement expected, JsonElement actual)
    {
        foreach (string property in new[] { "strokes", "shapes", "tombstones", "pending", "operations" })
        {
            Assert.True(JsonElement.DeepEquals(expected.GetProperty(property), actual.GetProperty(property)), property);
        }
    }

    private static async Task<JsonElement[]> WaitForMeshAsync(IReadOnlyList<SimulatorProcess> processes, int operationCount, int connections)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (true)
        {
            var stateTasks = new List<Task<JsonElement>>();
            foreach (var process in processes)
            {
                stateTasks.Add(process.CommandAsync("state"));
            }
            var states = await Task.WhenAll(stateTasks);
            bool allReady = true;
            foreach (var state in states)
            {
                if (state.GetProperty("operations").GetArrayLength() != operationCount || state.GetProperty("connections").GetInt32() != connections)
                {
                    allReady = false;
                }
            }
            if (allReady)
            {
                return states;
            }
            await Task.Delay(20, timeout.Token);
        }
    }

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

        public async Task<JsonElement> CommandAsync(string command, string? expectedEvent = null)
        {
            await process.StandardInput.WriteLineAsync(command.AsMemory(), TestContext.Current.CancellationToken);
            string eventName = expectedEvent ?? (command == "state" || command == "log" ? command : "done");
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
