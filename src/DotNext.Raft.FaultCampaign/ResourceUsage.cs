using System.Diagnostics;

namespace DotNext.Raft.FaultCampaign;

internal sealed class ResourceUsage
{
    public int Pid { get; init; }
    public long WorkingSetBytes { get; init; }
    public long ManagedBytes { get; init; }
    public long GcHeapBytes { get; init; }
    public int[] GcCollections { get; init; } = [];
    public double CpuSeconds { get; init; }
    public int Threads { get; init; }
    public int FileDescriptors { get; init; }
    public int SocketDescriptors { get; init; }
    public int VanishedDescriptors { get; init; }
    public int ThreadPoolThreads { get; init; }
    public long PendingWorkItems { get; init; }
    public long CompletedWorkItems { get; init; }
    public StorageUsage? Storage { get; init; }

    internal static ResourceUsage Capture(string? dataDirectory = null)
    {
        using var process = Process.GetCurrentProcess();
        var descriptors = 0;
        var sockets = 0;
        var vanished = 0;
        foreach (var path in Directory.EnumerateFiles("/proc/self/fd"))
        {
            descriptors++;
            var target = new FileInfo(path).LinkTarget;
            if (target is null)
                vanished++;
            else if (target.StartsWith("socket:[", StringComparison.Ordinal))
                sockets++;
        }

        return new()
        {
            Pid = process.Id,
            WorkingSetBytes = process.WorkingSet64,
            ManagedBytes = GC.GetTotalMemory(forceFullCollection: false),
            GcHeapBytes = GC.GetGCMemoryInfo().HeapSizeBytes,
            GcCollections = [GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)],
            CpuSeconds = process.TotalProcessorTime.TotalSeconds,
            Threads = Directory.EnumerateDirectories("/proc/self/task").Count(),
            FileDescriptors = descriptors,
            SocketDescriptors = sockets,
            VanishedDescriptors = vanished,
            ThreadPoolThreads = ThreadPool.ThreadCount,
            PendingWorkItems = ThreadPool.PendingWorkItemCount,
            CompletedWorkItems = ThreadPool.CompletedWorkItemCount,
            Storage = dataDirectory is null ? null : StorageUsage.Capture(dataDirectory),
        };
    }
}

internal sealed class StorageUsage
{
    public long WalBytes { get; set; }
    public int DataPages { get; set; }
    public int MetadataPages { get; set; }
    public long SnapshotBytes { get; set; }
    public int SnapshotFiles { get; set; }
    public int TemporaryFiles { get; set; }
    public long TemporaryBytes { get; set; }
    public int DisappearedFiles { get; set; }

    internal static StorageUsage Capture(string dataDirectory)
    {
        var result = new StorageUsage();
        foreach (var file in new DirectoryInfo(dataDirectory).EnumerateFiles("*", SearchOption.AllDirectories))
        {
            long length;
            try
            {
                length = file.Length;
            }
            catch (FileNotFoundException)
            {
                // Compaction and snapshot publication continue during this non-atomic sample.
                result.DisappearedFiles++;
                continue;
            }

            var relative = Path.GetRelativePath(dataDirectory, file.FullName);
            if (relative.StartsWith("wal" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                result.WalBytes += length;

            if (file.Extension is ".tmp")
            {
                result.TemporaryFiles++;
                result.TemporaryBytes += length;
            }
            else if (relative.StartsWith(Path.Combine("wal", "data") + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                result.DataPages++;
            }
            else if (relative.StartsWith(Path.Combine("wal", "metadata") + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                result.MetadataPages++;
            }
            else if (relative.StartsWith("sm" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                result.SnapshotFiles++;
                result.SnapshotBytes += length;
            }
        }

        return result;
    }
}
