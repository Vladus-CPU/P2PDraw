using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Xunit;

namespace P2PDraw.Tests.Integration;

public class SimulatorProcessTests
{
    [Fact]
    public async Task IndependentProcessesConvergeThroughTcp()
    {
        var session = Guid.NewGuid();
        await using var a = new SimulatorProcess(session);
        await using var b = new SimulatorProcess(session);
        var aReady = await a.ReadAsync("ready");
        var bReady = await b.ReadAsync("ready");
        Assert.NotEqual(aReady.GetProperty("peerId").GetGuid(), bReady.GetProperty("peerId").GetGuid());
        Assert.NotEqual(aReady.GetProperty("port").GetInt32(), bReady.GetProperty("port").GetInt32());
        await a.CommandAsync($"connect 127.0.0.1 {bReady.GetProperty("port").GetInt32()}");
        await WaitForStateAsync(a, b, 0, 1);
        var drawings = await Task.WhenAll(a.CommandAsync("draw 1 2 3 4"), b.CommandAsync("draw 5 6 7 8"));
        await WaitForStateAsync(a, b, 6, 1);
        await a.CommandAsync("duplicate");
        await b.CommandAsync($"remove {drawings[0].GetProperty("strokeId").GetGuid()}");
        var states = await WaitForStateAsync(a, b, 7, 1);
        Assert.Single(states[0].GetProperty("strokes").EnumerateArray());
        Assert.Single(states[0].GetProperty("tombstones").EnumerateArray());
        Assert.Equal(states[0].GetRawText(), states[1].GetRawText());

        await a.CommandAsync("disconnect");
        await WaitForStateAsync(a, b, 7, 0);
        await Task.WhenAll(a.CommandAsync("clear"), b.CommandAsync("draw 9 10 11 12"));
        await a.CommandAsync($"connect 127.0.0.1 {bReady.GetProperty("port").GetInt32()}");
        states = await WaitForStateAsync(a, b, 11, 1);
        Assert.Equal(states[0].GetRawText(), states[1].GetRawText());
        Assert.Single(states[0].GetProperty("strokes").EnumerateArray());
        Assert.Equal(2, states[0].GetProperty("tombstones").GetArrayLength());

        Guid survivor = states[0].GetProperty("strokes")[0].GetProperty("StrokeId").GetGuid();
        await a.CommandAsync("drop");
        await Task.WhenAll(a.CommandAsync("draw 13 14 15 16"), b.CommandAsync($"remove {survivor}"));
        states = await WaitForStateAsync(a, b, 15, 1);
        Assert.Equal(states[0].GetRawText(), states[1].GetRawText());
        Assert.Single(states[0].GetProperty("strokes").EnumerateArray());
        Assert.Equal(3, states[0].GetProperty("tombstones").GetArrayLength());
    }

    private static async Task<JsonElement[]> WaitForStateAsync(SimulatorProcess a, SimulatorProcess b, int operationCount, int connections)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (true)
        {
            var states = await Task.WhenAll(a.CommandAsync("state"), b.CommandAsync("state"));
            if (states.All(state => state.GetProperty("operations").GetArrayLength() == operationCount && state.GetProperty("connections").GetInt32() == connections)) return states;
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
                try { await process.WaitForExitAsync(timeout.Token); }
                catch (OperationCanceledException) { process.Kill(true); await process.WaitForExitAsync(TestContext.Current.CancellationToken); }
            }
            Assert.Equal(0, process.ExitCode);
            process.Dispose();
        }
    }
}
