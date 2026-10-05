using System.Diagnostics;
using System.Reflection;
using System.Runtime;
using System.Runtime.InteropServices;

namespace DotNext.Benchmarks.DurableWrite;

/// <summary>
/// Where the numbers come from: revision, OS, CPU, file system, runtime and GC.
/// </summary>
internal sealed class EnvironmentInfo
{
    public string? Revision { get; init; }
    public string? RevisionSource { get; init; }
    public required string Os { get; init; }
    public required string OsArchitecture { get; init; }
    public string? CpuModel { get; init; }
    public required int ProcessorCount { get; init; }
    public long? TotalMemoryBytes { get; init; }
    public required string Runtime { get; init; }
    public required string RuntimeIdentifier { get; init; }
    public required bool ServerGc { get; init; }
    public required bool ConcurrentGc { get; init; }
    public required string GcLatencyMode { get; init; }
    public required string WorkDirectory { get; init; }
    public string? FileSystem { get; init; }
    public string? Device { get; init; }
    public string? MountPoint { get; init; }
    public long? FreeBytesAtStart { get; init; }
    public string? CiRunner { get; init; }

    internal static EnvironmentInfo Collect(string workDirectory)
    {
        var (revision, source) = GetRevision();
        var (fileSystem, device, mountPoint, free) = GetFileSystem(workDirectory);
        return new()
        {
            Revision = revision,
            RevisionSource = source,
            Os = RuntimeInformation.OSDescription,
            OsArchitecture = RuntimeInformation.OSArchitecture.ToString(),
            CpuModel = GetCpuModel(),
            ProcessorCount = Environment.ProcessorCount,
            TotalMemoryBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
            Runtime = RuntimeInformation.FrameworkDescription,
            RuntimeIdentifier = RuntimeInformation.RuntimeIdentifier,
            ServerGc = GCSettings.IsServerGC,
            ConcurrentGc = AppContext.TryGetSwitch("System.GC.Concurrent", out var concurrent) ? concurrent : true,
            GcLatencyMode = GCSettings.LatencyMode.ToString(),
            WorkDirectory = workDirectory,
            FileSystem = fileSystem,
            Device = device,
            MountPoint = mountPoint,
            FreeBytesAtStart = free,
            CiRunner = Environment.GetEnvironmentVariable("RUNNER_NAME") is { Length: > 0 } runner
                ? $"{runner} ({Environment.GetEnvironmentVariable("ImageOS")} {Environment.GetEnvironmentVariable("ImageVersion")})".Trim()
                : null,
        };
    }

    private static (string?, string?) GetRevision()
    {
        if (Environment.GetEnvironmentVariable("GITHUB_SHA") is { Length: > 0 } sha)
            return (sha, "GITHUB_SHA");

        var informational = typeof(EnvironmentInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (informational?.IndexOf('+') is { } plus and >= 0 && plus + 1 < informational.Length)
            return (informational[(plus + 1)..], "assembly");

        try
        {
            using var git = Process.Start(new ProcessStartInfo("git", "rev-parse HEAD")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = AppContext.BaseDirectory,
            });

            if (git is not null && git.WaitForExit(2000) && git.ExitCode is 0)
                return (git.StandardOutput.ReadToEnd().Trim(), "git");
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // git is not installed
        }

        return (null, null);
    }

    private static string? GetCpuModel()
    {
        try
        {
            if (OperatingSystem.IsLinux() && File.Exists("/proc/cpuinfo"))
            {
                foreach (var line in File.ReadLines("/proc/cpuinfo"))
                {
                    if (line.StartsWith("model name", StringComparison.Ordinal) && line.IndexOf(':') is var colon and > 0)
                        return line[(colon + 1)..].Trim();
                }
            }
            else if (OperatingSystem.IsWindows())
            {
                return Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0",
                    "ProcessorNameString", null) as string is { } name ? name.Trim() : null;
            }
            else if (OperatingSystem.IsMacOS())
            {
                using var sysctl = Process.Start(new ProcessStartInfo("sysctl", "-n machdep.cpu.brand_string")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                });

                if (sysctl is not null && sysctl.WaitForExit(2000))
                    return sysctl.StandardOutput.ReadToEnd().Trim();
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or System.Security.SecurityException)
        {
            // not obtainable
        }

        return Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER");
    }

    private static (string? FileSystem, string? Device, string? MountPoint, long? Free) GetFileSystem(string workDirectory)
    {
        var path = Path.GetFullPath(workDirectory);
        string? fileSystem = null, device = null, mountPoint = null;
        long? free = null;
        try
        {
            if (OperatingSystem.IsLinux() && File.Exists("/proc/mounts"))
            {
                // The longest mount point that contains the work directory.
                foreach (var line in File.ReadLines("/proc/mounts"))
                {
                    var fields = line.Split(' ');
                    if (fields.Length < 3)
                        continue;

                    var point = fields[1].Replace("\\040", " ", StringComparison.Ordinal);
                    if (IsUnder(path, point) && (mountPoint is null || point.Length > mountPoint.Length))
                    {
                        mountPoint = point;
                        device = fields[0];
                        fileSystem = fields[2];
                    }
                }
            }

            var drive = new DriveInfo(Path.GetPathRoot(path)!);
            if (mountPoint is not null && !OperatingSystem.IsWindows())
                drive = new DriveInfo(mountPoint);

            fileSystem ??= drive.DriveFormat;
            mountPoint ??= drive.Name;
            device ??= $"{drive.DriveType}";
            free = drive.AvailableFreeSpace;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // not obtainable
        }

        return (fileSystem, device, mountPoint, free);

        static bool IsUnder(string path, string mountPoint)
            => mountPoint is "/" || path.Equals(mountPoint, StringComparison.Ordinal) || path.StartsWith(mountPoint + "/", StringComparison.Ordinal);
    }

    // The free space of the mount that holds the directory, which on Linux is not necessarily the root file system.
    internal static long? GetFreeBytes(string directory) => GetFileSystem(directory).Free;
}

/// <summary>
/// Measures what a synchronous flush to the device costs here, against a write that stays in the page cache.
/// </summary>
/// <remarks>
/// If an acknowledgment is cheaper than the flush it depends on, the numbers describe buffered writes, not durable
/// ones. The tool compares each cell with this calibration and marks it <c>suspectBuffered</c>. A device with a
/// volatile write cache that ignores flushes cannot be told apart from a fast durable one; that is recorded as a
/// residual blind spot.
/// </remarks>
internal sealed class FsyncProbe
{
    private const int Writes = 200;
    private const int BlockSize = 4096;

    public required LatencySummary Flushed { get; init; }
    public required LatencySummary Buffered { get; init; }

    // A flush is distinguishable from a buffered write on this device.
    public bool Distinguishable => Flushed.P50Us > 2L * long.Max(Buffered.P50Us, 1L);

    internal static FsyncProbe Run(string directory)
    {
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "fsync-probe.bin");
        var block = new byte[BlockSize];
        Random.Shared.NextBytes(block);
        var flushed = new LatencyHistogram();
        var buffered = new LatencyHistogram();
        try
        {
            using var stream = new FileStream(file, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 0, FileOptions.None);
            for (var i = 0; i < Writes; i++)
            {
                var start = Stopwatch.GetTimestamp();
                stream.Write(block);
                stream.Flush(flushToDisk: true);
                flushed.RecordTicks(Stopwatch.GetTimestamp() - start);

                start = Stopwatch.GetTimestamp();
                stream.Write(block);
                stream.Flush(flushToDisk: false);
                buffered.RecordTicks(Stopwatch.GetTimestamp() - start);
            }
        }
        finally
        {
            File.Delete(file);
        }

        return new() { Flushed = flushed.Summarize(), Buffered = buffered.Summarize() };
    }
}
