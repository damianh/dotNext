using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using System.Runtime.InteropServices;

namespace DotNext.Raft.FaultCampaign.Driver;

/// <summary>
/// One node of the cluster, as a sequence of OS processes (incarnations) that share a data directory.
/// </summary>
/// <remarks>
/// A node runs as <c>sh -c 'exec dotnet &lt;tool&gt; node ... &gt; log 2&gt;&amp;1'</c>, so the process the driver kills
/// is the node itself and its output goes straight to a file that survives the driver.
/// </remarks>
internal sealed partial class NodeProcess : IDisposable
{
    private const int SigTerm = 15;

    private readonly CampaignOptions options;
    private readonly HttpClient control;
    private readonly int[] raftPorts;
    private readonly List<Process> exited = [];
    private Process? process;
    private bool expectedExit;
    private int incarnations;

    internal NodeProcess(CampaignOptions options, int id, int[] raftPorts, int controlPort)
    {
        this.options = options;
        this.raftPorts = raftPorts;
        Id = id;
        ControlPort = controlPort;
        DataDirectory = Path.Combine(options.OutputDirectory, "data", $"node{id}");
        ClaimsFile = Path.Combine(options.OutputDirectory, "claims", $"node{id}.log");
        control = new()
        {
            BaseAddress = new($"http://127.0.0.1:{controlPort}/", UriKind.Absolute),

            // Longer than NodeHost.ReplicateTimeout, so a live node always answers a write with its outcome.
            Timeout = TimeSpan.FromSeconds(20),
        };
    }

    internal int Id { get; }

    internal int ControlPort { get; }

    internal string DataDirectory { get; }

    internal string ClaimsFile { get; }

    internal int Incarnations => incarnations;

    internal List<string> LogFiles { get; } = [];

    // Exits the driver did not cause: a crash, or a failed start.
    internal List<string> UnexpectedExits { get; } = [];

    internal bool IsRunning => process is { HasExited: false };

    internal int? Pid => process?.Id;

    internal void Start(NodeInjection injection)
    {
        if (IsRunning)
            throw new InvalidOperationException($"node {Id} is already running");

        CheckUnexpectedExit();

        // Not disposed until the node is: the monitor may still be reading the previous incarnation's process.
        if (process is not null)
            exited.Add(process);

        var incarnation = ++incarnations;
        var log = Path.Combine(options.OutputDirectory, "logs", $"node{Id}.{incarnation}.log");
        LogFiles.Add(log);

        var start = new ProcessStartInfo("/bin/sh") { UseShellExecute = false };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("exec \"$@\" > \"$0\" 2>&1");
        start.ArgumentList.Add(log);
        foreach (var arg in SelfCommand())
            start.ArgumentList.Add(arg);

        start.ArgumentList.Add("node");
        Add(start, "--transport", options.Transport is Transport.Http ? "http" : "tcp");
        Add(start, "--id", Id.ToString(CultureInfo.InvariantCulture));
        Add(start, "--peers", string.Join(',', raftPorts));
        Add(start, "--control-port", ControlPort.ToString(CultureInfo.InvariantCulture));
        Add(start, "--data", DataDirectory);
        Add(start, "--claims", ClaimsFile);
        Add(start, "--snapshot-interval", options.SnapshotInterval.ToString(CultureInfo.InvariantCulture));
        Add(start, "--inject", injection switch
        {
            NodeInjection.DropApplied => "drop-applied",
            NodeInjection.VolatileStorage => "volatile-storage",
            _ => "none",
        });

        expectedExit = false;
        process = Process.Start(start) ?? throw new InvalidOperationException($"node {Id} did not start");

        static void Add(ProcessStartInfo start, string name, string value)
        {
            start.ArgumentList.Add(name);
            start.ArgumentList.Add(value);
        }
    }

    // The command that runs this tool: the apphost, or 'dotnet <dll>'.
    private static IEnumerable<string> SelfCommand()
    {
        var host = Environment.ProcessPath ?? "dotnet";
        yield return host;
        if (Path.GetFileNameWithoutExtension(host) is "dotnet")
            yield return typeof(NodeProcess).Assembly.Location;
    }

    /// <summary>
    /// SIGKILL: the process ends without running any code.
    /// </summary>
    internal async Task KillAsync()
    {
        if (process is not { HasExited: false } p)
            return;

        expectedExit = true;
        p.Kill(entireProcessTree: false);
        await p.WaitForExitAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// SIGTERM: the host stops the cluster and disposes the log. A node that is still running after
    /// <paramref name="grace"/> is reported and killed.
    /// </summary>
    /// <returns><see langword="true"/> if the node exited by itself.</returns>
    internal async Task<bool> TerminateAsync(TimeSpan grace)
    {
        if (process is not { HasExited: false } p)
            return true;

        expectedExit = true;
        if (Kill(p.Id, SigTerm) is not 0)
            throw new InvalidOperationException($"kill({p.Id}, SIGTERM) failed with errno {Marshal.GetLastPInvokeError()}");

        using var timeout = new CancellationTokenSource(grace);
        try
        {
            await p.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            if (p.ExitCode is not 0)
                UnexpectedExits.Add($"node {Id} incarnation {incarnations} exited with code {p.ExitCode} after SIGTERM");

            return true;
        }
        catch (OperationCanceledException)
        {
            UnexpectedExits.Add($"node {Id} incarnation {incarnations} did not stop within {grace.TotalSeconds:F0} s of SIGTERM");
            await KillAsync().ConfigureAwait(false);
            return false;
        }
    }

    /// <summary>
    /// Records an exit the driver did not cause.
    /// </summary>
    internal void CheckUnexpectedExit()
    {
        if (process is { HasExited: true } p && !expectedExit)
        {
            expectedExit = true;
            UnexpectedExits.Add($"node {Id} incarnation {incarnations} (pid {p.Id}) exited by itself with code {p.ExitCode}");
        }
    }

    internal async Task<NodeStatus?> GetStatusAsync(CancellationToken token)
    {
        if (!IsRunning)
            return null;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            return await control.GetFromJsonAsync<NodeStatus>(ControlApi.Status, ControlApi.Json, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException && !token.IsCancellationRequested)
        {
            return null;
        }
    }

    internal async Task<HistoryPage?> GetHistoryAsync(int from, Guid incarnation, int epoch, CancellationToken token)
    {
        if (!IsRunning)
            return null;

        var uri = string.Create(CultureInfo.InvariantCulture, $"{ControlApi.History}?from={from}&incarnation={incarnation}&epoch={epoch}");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            return await control.GetFromJsonAsync<HistoryPage>(uri, ControlApi.Json, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException && !token.IsCancellationRequested)
        {
            return null;
        }
    }

    internal async Task<WriteOutcome> WriteAsync(ReadOnlyMemory<byte> payload, CancellationToken token)
    {
        using var content = new ReadOnlyMemoryContent(payload);
        try
        {
            using var response = await control.PostAsync(ControlApi.Write, content, token).ConfigureAwait(false);
            return (int)response.StatusCode switch
            {
                ControlApi.Acknowledged => WriteOutcome.Acknowledged,
                ControlApi.Rejected => WriteOutcome.Rejected,
                _ => WriteOutcome.Unknown,
            };
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException && !token.IsCancellationRequested)
        {
            // The node died or did not answer in time: the write may still be committed.
            return WriteOutcome.Unknown;
        }
    }

    public void Dispose()
    {
        if (process is { HasExited: false } p)
        {
            expectedExit = true;
            p.Kill();
            p.WaitForExit();
        }

        process?.Dispose();
        foreach (var old in exited)
            old.Dispose();

        control.Dispose();
    }

    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static partial int Kill(int pid, int signal);
}

internal enum WriteOutcome
{
    Acknowledged,
    Rejected,
    Unknown,
}
