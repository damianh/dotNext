using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;

namespace DotNext.Net.Cluster.Consensus.Raft.StateMachine;

using InProcess;

public sealed class WalCrashWorker : Test
{
    private const string ConfigurationVariable = "DOTNEXT_WAL_CRASH_CONFIGURATION";
    private const string PipeVariable = "DOTNEXT_WAL_CRASH_PIPE";

    internal sealed record Configuration(string Location, WriteAheadLog.MemoryManagementStrategy Strategy,
        bool Direct, int FlushMode, WriteAheadLog.IntegrityHashAlgorithm Hash, string Scenario);

    [Fact]
    public static async Task Run()
    {
        var configuration = Environment.GetEnvironmentVariable(ConfigurationVariable);
        if (configuration is null)
            Skip("Executed only by the isolated WAL crash tests.");
        var settings = JsonSerializer.Deserialize<Configuration>(configuration);
        NotNull(settings);
        if (settings.Scenario is "raft")
        {
            await AcknowledgedLogDurabilityTests.RunAsync(
                Enumerable.Range(0, 3).Select(i => Path.Combine(settings.Location, i.ToString())).ToArray(),
                ReadyAsync);
        }
        else
        {
            await WriteAheadLogDurabilityTests.SeedPrefixAsync(WriteAheadLogDurabilityTests.CreateOptions(
                settings.Location, settings.Strategy, settings.Direct, 0, settings.Hash));
            await using var wal = new WriteAheadLog(WriteAheadLogDurabilityTests.CreateOptions(
                settings.Location, settings.Strategy, settings.Direct, settings.FlushMode, settings.Hash), IStateMachine.CreateNoOp());
            await wal.InitializeAsync(TestToken);
            if (settings.Scenario is "overwrite")
            {
                await WriteAheadLogDurabilityTests.InterruptReplacementAsync(wal, ReadyAsync);
                return;
            }
            await wal.AppendAsync(new TestLogEntry("acknowledged tail") { Term = 1L }, TestToken);
            Equal(2L, wal.LastEntryIndex);
            Equal(1L, wal.LastCommittedEntryIndex);
            await ReadyAsync();
        }
    }

    private static async Task ReadyAsync()
    {
        await using var pipe = new NamedPipeClientStream(".", Environment.GetEnvironmentVariable(PipeVariable),
            PipeDirection.Out, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(TestToken);
        await pipe.WriteAsync(new byte[] { 1 }, TestToken);
        await pipe.FlushAsync(TestToken);
        await Task.Delay(Timeout.InfiniteTimeSpan, TestToken);
    }

    internal static async Task KillAfterAcknowledgmentAsync(Configuration configuration)
    {
        var pipeName = "dotnext-wal-" + Guid.NewGuid().ToString("N");
        await using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.In, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in new[]
        {
            "exec", typeof(WalCrashWorker).Assembly.Location, "--filter-class", typeof(WalCrashWorker).FullName,
            "--progress", "off", "--timeout", "90s",
        })
            start.ArgumentList.Add(argument);
        start.Environment[ConfigurationVariable] = JsonSerializer.Serialize(configuration);
        start.Environment[PipeVariable] = pipeName;
        using var process = Process.Start(start);
        NotNull(process);
        var stdout = process.StandardOutput.ReadToEndAsync(TestToken);
        var stderr = process.StandardError.ReadToEndAsync(TestToken);
        try
        {
            var connection = pipe.WaitForConnectionAsync(TestToken);
            var exited = process.WaitForExitAsync(TestToken);
            var completed = await Task.WhenAny(connection, exited).WaitAsync(DefaultTimeout, TestToken);
            if (completed == exited)
                Fail($"Crash worker exited before acknowledgment: {await stdout}\n{await stderr}");
            await connection;
            var acknowledgment = new byte[1];
            await pipe.ReadExactlyAsync(acknowledgment, TestToken).AsTask().WaitAsync(DefaultTimeout, TestToken);
            Equal(1, acknowledgment[0]);
            process.Kill(entireProcessTree: true);
            await exited.WaitAsync(DefaultTimeout, TestToken);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None).WaitAsync(DefaultTimeout);
            }
            await Task.WhenAll(stdout, stderr);
        }
    }
}
