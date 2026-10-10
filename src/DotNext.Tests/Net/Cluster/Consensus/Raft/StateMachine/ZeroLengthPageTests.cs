namespace DotNext.Net.Cluster.Consensus.Raft.StateMachine;

using IO;

/// <summary>
/// A process killed inside <c>AnonymousPage.FlushAsync</c>, between <c>File.OpenHandle(FileMode.OpenOrCreate)</c> and
/// <c>RandomAccess.SetLength</c>, leaves a zero-length data page file. The WAL must still open after the restart.
/// </summary>
/// <remarks>
/// The crash states are written to disk after an orderly close, like the red test: a process kill loses nothing
/// that was already handed to the kernel, so the files left by a kill at each step are known exactly (#148).
/// </remarks>
[Collection(TestCollections.WriteAheadLog)]
public sealed class ZeroLengthPageTests : Test
{
    private const int ChunkSize = 4096;
    private const int MetadataPageSize = 4096;
    private const int EntryCount = 80; // crosses the first data page and the first metadata page
    private const string Data = "data", Metadata = "metadata";

    public enum CrashStep
    {
        // fork builds before #148: the final file was created, but its length was never set
        EmptyPage,

        // the temporary file is created
        EmptyTemporaryFile,

        // the temporary file is sized (and flushed)
        SizedTemporaryFile,

        // the page is published, and a sized temporary file of the same page is left next to it
        // (not produced by rename(2) or MoveFileEx, but must be tolerated all the same)
        PublishedWithTemporaryFile,

        // published, and the directory is flushed, but the page has not been written yet
        Published,
    }

    private static IEnumerable<(WriteAheadLog.MemoryManagementStrategy, bool)> GetStrategies()
    {
        yield return (WriteAheadLog.MemoryManagementStrategy.PrivateMemory, false);
        yield return (WriteAheadLog.MemoryManagementStrategy.SharedMemory, false);

        if (OperatingSystem.IsLinux() || OperatingSystem.IsWindows())
            yield return (WriteAheadLog.MemoryManagementStrategy.PrivateMemory, true);
    }

    public static TheoryData<WriteAheadLog.MemoryManagementStrategy, bool> Strategies
    {
        get
        {
            var result = new TheoryData<WriteAheadLog.MemoryManagementStrategy, bool>();
            foreach (var (strategy, direct) in GetStrategies())
                result.Add(strategy, direct);

            return result;
        }
    }

    public static TheoryData<WriteAheadLog.MemoryManagementStrategy, bool, CrashStep, string> CrashSteps
    {
        get
        {
            var result = new TheoryData<WriteAheadLog.MemoryManagementStrategy, bool, CrashStep, string>();
            foreach (var (strategy, direct) in GetStrategies())
            foreach (var step in Enum.GetValues<CrashStep>())
            foreach (var kind in new[] { Data, Metadata })
                result.Add(strategy, direct, step, kind);

            return result;
        }
    }

    public static TheoryData<string, long, bool> MismatchedLengths
    {
        get
        {
            var result = new TheoryData<string, long, bool>();
            foreach (var kind in new[] { Data, Metadata })
            foreach (var length in new long[] { 1L, ChunkSize - 1L, ChunkSize + 1L, ChunkSize * 2L })
            foreach (var referenced in new[] { true, false })
                result.Add(kind, length, referenced);

            return result;
        }
    }

    private static WriteAheadLog.Options CreateOptions(string location,
        WriteAheadLog.MemoryManagementStrategy strategy = WriteAheadLog.MemoryManagementStrategy.PrivateMemory,
        bool direct = false) => new()
    {
        Location = location,
        ChunkSize = ChunkSize,
        MemoryManagement = strategy,
        NoBuffering = direct,
        HashAlgorithm = WriteAheadLog.IntegrityHashAlgorithm.Crc64,
    };

    // no snapshots, so the entries keep their payloads
    private static IStateMachine CreateStateMachine() => IStateMachine.CreateNoOp(snapshotThreshold: 1_000_000L);

    private static ReadOnlyMemory<byte> Payload(long index)
        => Enumerable.Repeat((byte)index, 100).ToArray();

    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task OpensAfterCrashBetweenPageCreateAndResize()
    {
        var location = GetTempPath();
        ReadOnlyMemory<byte> payload = Enumerable.Repeat((byte)0x5A, 100).ToArray();

        await using (var wal = new WriteAheadLog(new() { Location = location, ChunkSize = ChunkSize }, IStateMachine.CreateNoOp()))
        {
            Equal(1L, await wal.AppendAsync(new BinaryLogEntry { Content = payload, Term = 1L }, TestToken));
            await wal.CommitAsync(1L, TestToken);
            await wal.FlushAsync(TestToken);
        }

        // The crash state: the next page file exists, but its length was never set.
        var data = new DirectoryInfo(Path.Combine(location, "data"));
        var last = data.EnumerateFiles().Select(static f => uint.Parse(f.Name)).Max();
        File.Create(Path.Combine(data.FullName, (last + 1U).ToString())).Dispose();

        await using (var wal = new WriteAheadLog(new() { Location = location, ChunkSize = ChunkSize }, IStateMachine.CreateNoOp()))
        {
            Equal(1L, wal.LastEntryIndex);
            using var entries = await wal.ReadAsync(1L, 1L, TestToken);
            Equal(payload.ToArray(), await entries[0].ToByteArrayAsync(token: TestToken));
        }
    }

    [Theory(Timeout = TestTimeouts.Default)]
    [MemberData(nameof(CrashSteps))]
    public static async Task RestartsAfterCrashDuringPageCreation(WriteAheadLog.MemoryManagementStrategy strategy,
        bool direct, CrashStep step, string kind)
    {
        var options = CreateOptions(GetTempPath(), strategy, direct);
        await AppendAsync(options, 1L, 1L);

        var directory = new DirectoryInfo(Path.Combine(options.Location, kind));
        var pageIndex = GetPages(directory).Max() + 1U;
        var page = Path.Combine(directory.FullName, pageIndex.ToString());
        var temporary = $"{page}.{Path.GetRandomFileName()}.tmp";
        var pageSize = kind is Data ? ChunkSize : MetadataPageSize;
        switch (step)
        {
            case CrashStep.EmptyPage:
                await File.WriteAllBytesAsync(page, [], TestToken);
                break;
            case CrashStep.EmptyTemporaryFile:
                await File.WriteAllBytesAsync(temporary, [], TestToken);
                break;
            case CrashStep.SizedTemporaryFile:
                await File.WriteAllBytesAsync(temporary, new byte[pageSize], TestToken);
                break;
            case CrashStep.PublishedWithTemporaryFile:
                await File.WriteAllBytesAsync(temporary, new byte[pageSize], TestToken);
                await File.WriteAllBytesAsync(page, new byte[pageSize], TestToken);
                break;
            case CrashStep.Published:
                await File.WriteAllBytesAsync(page, new byte[pageSize], TestToken);
                break;
        }

        // The restarted WAL reads the durable entry and writes on into the page that was being created.
        await AppendAsync(options, 1L, EntryCount, expectedLastIndex: 1L);
        await AssertEntriesAsync(options, EntryCount);
        AssertCompletePages(options.Location);
    }

    [Theory(Timeout = TestTimeouts.Default)]
    [MemberData(nameof(Strategies))]
    public static async Task RestartsAfterCrashInFirstAppend(WriteAheadLog.MemoryManagementStrategy strategy, bool direct)
    {
        // A new store: the checkpoint is still empty, and the first page of each kind was left empty.
        var options = CreateOptions(GetTempPath(), strategy, direct);
        await using (new WriteAheadLog(options, CreateStateMachine()))
        {
        }

        foreach (var kind in new[] { Data, Metadata })
            await File.WriteAllBytesAsync(Path.Combine(options.Location, kind, "0"), [], TestToken);

        await AppendAsync(options, 1L, EntryCount, expectedLastIndex: 0L);
        await AssertEntriesAsync(options, EntryCount);
        AssertCompletePages(options.Location);
    }

    [Theory(Timeout = TestTimeouts.Default)]
    [InlineData(Data, 0U)]
    [InlineData(Data, 1U)]
    [InlineData(Metadata, 0U)]
    [InlineData(Metadata, 1U)]
    public static async Task ReferencedEmptyPageFailsClosed(string kind, uint pageIndex)
    {
        var options = CreateOptions(GetTempPath());
        await AppendAsync(options, 1L, EntryCount);

        // Both pages hold durable entries: the last entry ends in page 1 of each kind.
        var page = Path.Combine(options.Location, kind, pageIndex.ToString());
        True(File.Exists(page));
        await File.WriteAllBytesAsync(page, [], TestToken);

        var e = Throws<InvalidDataException>(() => new WriteAheadLog(options, CreateStateMachine()));
        Contains("is empty", e.Message);
        True(File.Exists(page));
    }

    [Theory(Timeout = TestTimeouts.Default)]
    [MemberData(nameof(MismatchedLengths))]
    public static async Task MismatchedPageLengthIsRejected(string kind, long length, bool referenced)
    {
        var options = CreateOptions(GetTempPath());
        await AppendAsync(options, 1L, 1L);

        var directory = Path.Combine(options.Location, kind);
        var page = Path.Combine(directory, (referenced ? 0U : GetPages(new(directory)).Max() + 1U).ToString());
        await File.WriteAllBytesAsync(page, new byte[length], TestToken);

        var e = Throws<InvalidDataException>(() => new WriteAheadLog(options, CreateStateMachine()));
        Contains($"has length {length}", e.Message);
        Equal(length, new FileInfo(page).Length);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task UnrelatedTemporaryFilesAreKept()
    {
        var options = CreateOptions(GetTempPath());
        await AppendAsync(options, 1L, 1L);
        var unrelated = Path.Combine(options.Location, Data, "unrelated.tmp");
        await File.WriteAllBytesAsync(unrelated, [], TestToken);

        await AssertEntriesAsync(options, 1L);
        True(File.Exists(unrelated));
    }

    private static async Task AppendAsync(WriteAheadLog.Options options, long first, long last, long? expectedLastIndex = null)
    {
        await using var wal = new WriteAheadLog(options, CreateStateMachine());
        if (expectedLastIndex.HasValue)
            Equal(expectedLastIndex.GetValueOrDefault(), wal.LastEntryIndex);

        for (var index = long.Max(first, wal.LastEntryIndex + 1L); index <= last; index++)
            Equal(index, await wal.AppendAsync(new BinaryLogEntry { Content = Payload(index), Term = 1L }, TestToken));

        await wal.CommitAsync(last, TestToken);
        await wal.FlushAsync(TestToken);
    }

    private static async Task AssertEntriesAsync(WriteAheadLog.Options options, long last)
    {
        await using var wal = new WriteAheadLog(options, CreateStateMachine());
        Equal(last, wal.LastEntryIndex);
        using var entries = await wal.ReadAsync(1L, last, TestToken);
        for (var index = 1L; index <= last; index++)
            Equal(Payload(index).ToArray(), await entries[(int)(index - 1L)].ToByteArrayAsync(token: TestToken));
    }

    private static void AssertCompletePages(string location)
    {
        foreach (var (kind, pageSize) in new[] { (Data, ChunkSize), (Metadata, MetadataPageSize) })
        {
            var directory = new DirectoryInfo(Path.Combine(location, kind));
            Empty(directory.EnumerateFiles("*.tmp"));
            NotEmpty(GetPages(directory));
            All(directory.EnumerateFiles(), file => Equal(pageSize, file.Length));
        }
    }

    private static IEnumerable<uint> GetPages(DirectoryInfo directory)
        => directory.EnumerateFiles().Select(static f => uint.TryParse(f.Name, out var i) ? i : (uint?)null)
            .Where(static i => i.HasValue).Select(static i => i.GetValueOrDefault());
}
