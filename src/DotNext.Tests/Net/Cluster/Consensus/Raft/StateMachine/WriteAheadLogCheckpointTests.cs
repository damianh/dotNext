using System.Buffers.Binary;
using System.IO.Hashing;
using System.Reflection;
using System.Runtime.ExceptionServices;

namespace DotNext.Net.Cluster.Consensus.Raft.StateMachine;

using IO.Log;

[Collection(TestCollections.WriteAheadLog)]
public sealed class WriteAheadLogCheckpointTests : Test
{
    private const int BlockSize = 64 * 1024;
    private const int FileSize = BlockSize * 3;

    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    [InlineData(12)]
    public static async Task LegacyUpgradePreservesIndependentBoundaries(int legacySize)
    {
        var location = CreateLocation();
        var original = new byte[legacySize];
        if (legacySize > 0)
            BinaryPrimitives.WriteInt64LittleEndian(original.AsSpan(legacySize - sizeof(long)), 7L);
        File.WriteAllBytes(Path.Combine(location.FullName, "checkpoint"), original);
        File.WriteAllBytes(Path.Combine(location.FullName, "checkpoint.prepared"), [1, 0, 0, 0, 0]);

        using (var checkpoint = new CheckpointFile(location))
        {
            Equal(0U, checkpoint.Version);
            Equal(0L, checkpoint.Generation);
            Equal(legacySize == 0 ? 0L : 7L, checkpoint.Read<long>("Checkpoint"));
            Equal(original, ReadBytes(Path.Combine(location.FullName, "checkpoint")));

            await checkpoint.UpdateAsync(7L, 9L, 1024UL, 3L, 1L, TestToken);
            Equal(1U, checkpoint.Version);
            Equal(1L, checkpoint.Generation);
        }

        var bytes = File.ReadAllBytes(Path.Combine(location.FullName, "checkpoint"));
        Equal(FileSize, bytes.Length);
        Equal(1U, BinaryPrimitives.ReadUInt32LittleEndian(bytes));
        True(File.Exists(Path.Combine(location.FullName, "checkpoint.format")));
        False(File.Exists(Path.Combine(location.FullName, "checkpoint.prepared")));
        using var recovered = new CheckpointFile(location);
        Equal(1L, recovered.Generation);
        Equal(7L, recovered.Read<long>("Checkpoint"));
        Equal(9L, recovered.Read<long>("LastIndex"));
        Equal(1024UL, recovered.Read<ulong>("WritePosition"));
        Equal(3L, recovered.Read<long>("SnapshotIndex"));
    }

    [Fact]
    public static async Task GenerationsAlternateAndRecoverShorterUncommittedTail()
    {
        var location = CreateLocation();
        using (var checkpoint = new CheckpointFile(location))
        {
            await checkpoint.UpdateAsync(0L, 2L, 100UL, 0L, 1L, TestToken);
            await checkpoint.UpdateAsync(1L, 4L, 200UL, 0L, 2L, TestToken);
            await checkpoint.UpdateAsync(1L, 3L, 300UL, 1L, 3L, TestToken);
            Equal(3L, checkpoint.Generation);
        }

        var bytes = File.ReadAllBytes(Path.Combine(location.FullName, "checkpoint"));
        Equal(2L, BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(BlockSize + 64)));
        Equal(3L, BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(BlockSize * 2 + 64)));
        False(File.Exists(Path.Combine(location.FullName, "checkpoint.pending")));
        using var recovered = new CheckpointFile(location);
        Equal(3L, recovered.Generation);
        Equal(1L, recovered.Read<long>("Checkpoint"));
        Equal(3L, recovered.Read<long>("LastIndex"));
        Equal(300UL, recovered.Read<ulong>("WritePosition"));
        Equal(1L, recovered.Read<long>("SnapshotIndex"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public static async Task CorruptedCompletedSlotCannotSilentlyLoseAcknowledgedHistory(int slot)
    {
        var location = CreateLocation();
        await CreateVersion1(location);
        var path = Path.Combine(location.FullName, "checkpoint");
        var bytes = File.ReadAllBytes(path);
        bytes[(slot + 1) * BlockSize + 40] ^= 0x80;
        File.WriteAllBytes(path, bytes);

        Throws<IntegrityException>(() => new CheckpointFile(location));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public static async Task ZeroedCompletedSlotIsNotAnUnusedGeneration(int slot)
    {
        var location = CreateLocation();
        await CreateVersion1(location);
        var path = Path.Combine(location.FullName, "checkpoint");
        var bytes = File.ReadAllBytes(path);
        bytes.AsSpan((slot + 1) * BlockSize, BlockSize).Clear();
        File.WriteAllBytes(path, bytes);

        Throws<IntegrityException>(() => new CheckpointFile(location));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(12)]
    [InlineData(BlockSize)]
    [InlineData(FileSize - 1)]
    [InlineData(FileSize + 1)]
    public static async Task TruncatedOrExtendedVersion1CannotMasqueradeAsLegacy(int size)
    {
        var location = CreateLocation();
        await CreateVersion1(location);
        using (var file = File.OpenWrite(Path.Combine(location.FullName, "checkpoint")))
            file.SetLength(size);

        Throws<IntegrityException>(() => new CheckpointFile(location));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(7)]
    [InlineData(9)]
    [InlineData(11)]
    [InlineData(13)]
    public static void InvalidLegacyLengthIsRejected(int size)
    {
        var location = CreateLocation();
        File.WriteAllBytes(Path.Combine(location.FullName, "checkpoint"), new byte[size]);
        Throws<IntegrityException>(() => new CheckpointFile(location));
    }

    [Theory]
    [InlineData(8)]
    [InlineData(12)]
    public static void NegativeLegacyIndexIsRejected(int size)
    {
        var location = CreateLocation();
        var bytes = new byte[size];
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(size - sizeof(long)), -1L);
        File.WriteAllBytes(Path.Combine(location.FullName, "checkpoint"), bytes);
        Throws<IntegrityException>(() => new CheckpointFile(location));
    }

    [Fact]
    public static async Task UnsupportedVersionIsRetainedWithoutMutatingTheStore()
    {
        var location = CreateLocation();
        var bytes = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 37U);
        File.WriteAllBytes(Path.Combine(location.FullName, "checkpoint"), bytes);
        using var checkpoint = new CheckpointFile(location);
        Equal(37U, checkpoint.Version);
        Null(checkpoint.Current);
        var error = await ThrowsAsync<WriteAheadLog.UnsupportedCheckpointVersionException>(
            () => checkpoint.UpdateAsync(0L, 0L, 0UL, 0L, 1L, TestToken).AsTask());
        Equal(37U, error.Version);
        Equal(bytes, ReadBytes(Path.Combine(location.FullName, "checkpoint")));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public static async Task InterruptedPublicationUsesOnlyTheIntendedSlot(int publicationStage)
    {
        var location = CreateLocation();
        var path = Path.Combine(location.FullName, "checkpoint");
        byte[] before, after;
        using (var checkpoint = new CheckpointFile(location))
        {
            await checkpoint.UpdateAsync(0L, 1L, 100UL, 0L, 1L, TestToken);
            await checkpoint.UpdateAsync(1L, 2L, 200UL, 1L, 2L, TestToken);
            before = ReadBytes(path);
            await checkpoint.UpdateAsync(1L, 3L, 300UL, 1L, 3L, TestToken);
            after = ReadBytes(path);
        }

        // Reconstruct the three crash boundaries using records produced by the
        // real writer: durable intent, torn target, and durable target.
        var count = publicationStage switch
        {
            0 => 0,
            1 => 69,
            _ => BlockSize,
        };
        after.AsSpan(2 * BlockSize, count).CopyTo(before.AsSpan(2 * BlockSize));
        File.WriteAllBytes(path, before);
        WriteIntent(location, before, 2L);

        using (var recovered = new CheckpointFile(location))
        {
            var generation = publicationStage == 2 ? 3L : 2L;
            Equal(generation, recovered.Generation);
            Equal(generation, recovered.Read<long>("LastIndex"));
            Equal(1L, recovered.Read<long>("Checkpoint"));
            await recovered.UpdateAsync(1L, 4L, 400UL, 1L, generation + 1L, TestToken);
        }

        using var reopenedAgain = new CheckpointFile(location);
        Equal(4L, reopenedAgain.Read<long>("LastIndex"));
        False(File.Exists(Path.Combine(location.FullName, "checkpoint.pending")));
    }

    [Fact]
    public static async Task IntentDoesNotExcuseCorruptionOfTheStableGeneration()
    {
        var location = CreateLocation();
        await CreateVersion1(location);
        var path = Path.Combine(location.FullName, "checkpoint");
        var bytes = File.ReadAllBytes(path);
        WriteIntent(location, bytes, 2L);
        bytes[BlockSize + 40] ^= 0x80;
        bytes[2 * BlockSize + 40] ^= 0x80;
        File.WriteAllBytes(path, bytes);
        Throws<IntegrityException>(() => new CheckpointFile(location));
    }

    [Theory]
    [InlineData(32, -1L)]
    [InlineData(40, 0L)]
    [InlineData(56, 3L)]
    [InlineData(64, 0L)]
    public static async Task ChecksummedInvalidBoundariesAreStillRejected(int fieldOffset, long value)
    {
        var location = CreateLocation();
        await CreateVersion1(location);
        var path = Path.Combine(location.FullName, "checkpoint");
        var bytes = File.ReadAllBytes(path);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(BlockSize + fieldOffset), value);
        Seal(bytes.AsSpan(BlockSize, BlockSize));
        File.WriteAllBytes(path, bytes);
        Throws<IntegrityException>(() => new CheckpointFile(location));
    }

    [Fact]
    public static async Task ChecksummedUnsupportedSlotVersionRetainsExceptionType()
    {
        var location = CreateLocation();
        await CreateVersion1(location);
        var path = Path.Combine(location.FullName, "checkpoint");
        var bytes = File.ReadAllBytes(path);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(BlockSize), 37U);
        Seal(bytes.AsSpan(BlockSize, BlockSize));
        File.WriteAllBytes(path, bytes);
        var error = Throws<WriteAheadLog.UnsupportedCheckpointVersionException>(() => new CheckpointFile(location));
        Equal(37U, error.Version);
    }

    [Fact]
    public static async Task HeaderIntegrityDoesNotDependOnEntryHashing()
    {
        var location = CreateLocation();
        await CreateVersion1(location);
        var path = Path.Combine(location.FullName, "checkpoint");
        var bytes = File.ReadAllBytes(path);
        bytes[16] ^= 0x80;
        File.WriteAllBytes(path, bytes);
        Throws<IntegrityException>(() => new CheckpointFile(location));
    }

    [Fact]
    public static async Task SidecarsFromAnotherStoreAreRejected()
    {
        var location = CreateLocation();
        var other = CreateLocation();
        await CreateVersion1(location);
        await CreateVersion1(other);
        File.Copy(Path.Combine(other.FullName, "checkpoint.format"),
            Path.Combine(location.FullName, "checkpoint.format"), overwrite: true);
        Throws<IntegrityException>(() => new CheckpointFile(location));
    }

    [Theory]
    [InlineData("checkpoint.format")]
    [InlineData("checkpoint.pending")]
    public static async Task InvalidSidecarIsRejected(string sidecar)
    {
        var location = CreateLocation();
        await CreateVersion1(location);
        File.WriteAllBytes(Path.Combine(location.FullName, sidecar), [1, 2, 3]);
        Throws<IntegrityException>(() => new CheckpointFile(location));
    }

    [Fact]
    public static async Task UnpublishedSidecarsDoNotReplaceAValidGeneration()
    {
        var location = CreateLocation();
        await CreateVersion1(location);
        File.WriteAllBytes(Path.Combine(location.FullName, "checkpoint.pending.prepared"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(location.FullName, "checkpoint.format.prepared"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(location.FullName, "checkpoint.prepared"), [1, 2, 3]);
        using var recovered = new CheckpointFile(location);
        Equal(2L, recovered.Generation);
    }

    [Fact]
    public static async Task UpgradeInterruptedBeforeFormatMarkerStillRecovers()
    {
        var location = CreateLocation();
        await CreateVersion1(location);
        File.Delete(Path.Combine(location.FullName, "checkpoint.format"));
        using (var recovered = new CheckpointFile(location))
        {
            Equal(2L, recovered.Generation);
            await recovered.UpdateAsync(1L, 3L, 300UL, 1L, 3L, TestToken);
        }

        True(File.Exists(Path.Combine(location.FullName, "checkpoint.format")));
        using var reopenedAgain = new CheckpointFile(location);
        Equal(3L, reopenedAgain.Generation);
    }

    [Theory]
    [InlineData("metadata")]
    [InlineData("data")]
    public static void MissingPrimaryWithExistingPagesIsNotANewStore(string directory)
    {
        var location = CreateLocation();
        var pages = Directory.CreateDirectory(Path.Combine(location.FullName, directory));
        File.WriteAllBytes(Path.Combine(pages.FullName, "0"), [1]);
        Throws<IntegrityException>(() => new CheckpointFile(location));
        False(File.Exists(Path.Combine(location.FullName, "checkpoint")));
    }

    [Fact]
    public static async Task ValidationAndCancellationDoNotAdvanceGeneration()
    {
        var location = CreateLocation();
        using var checkpoint = new CheckpointFile(location);
        await ThrowsAsync<IntegrityException>(
            () => checkpoint.UpdateAsync(2L, 1L, 0UL, 0L, 1L, TestToken).AsTask());
        await ThrowsAsync<ArgumentOutOfRangeException>(
            () => checkpoint.UpdateAsync(0L, 1L, 0UL, 0L, 2L, TestToken).AsTask());
        await ThrowsAnyAsync<OperationCanceledException>(
            () => checkpoint.UpdateAsync(0L, 1L, 0UL, 0L, 1L, new CancellationToken(true)).AsTask());
        Equal(0L, checkpoint.Generation);
        Equal(0L, new FileInfo(Path.Combine(location.FullName, "checkpoint")).Length);
        False(File.Exists(Path.Combine(location.FullName, "checkpoint.format")));
        False(File.Exists(Path.Combine(location.FullName, "checkpoint.pending")));
    }

    [Fact]
    public static void PublicationPrimitiveReplacesAnExistingFile()
    {
        var location = CreateLocation();
        var temporary = Path.Combine(location.FullName, "journal.prepared");
        var destination = Path.Combine(location.FullName, "journal");
        File.WriteAllBytes(temporary, [4, 5, 6]);
        File.WriteAllBytes(destination, [1, 2, 3]);
        var checkpoint = typeof(WriteAheadLog).GetNestedType("Checkpoint", BindingFlags.NonPublic)!;
        checkpoint.GetMethod("PublishFile", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [temporary, destination]);
        Equal(new byte[] { 4, 5, 6 }, File.ReadAllBytes(destination));
        False(File.Exists(temporary));
    }

    [Fact]
    public static void PublicationPrimitiveRejectsDifferentDirectories()
    {
        var sourceLocation = CreateLocation();
        var destinationLocation = CreateLocation();
        var temporary = Path.Combine(sourceLocation.FullName, "journal.prepared");
        var destination = Path.Combine(destinationLocation.FullName, "journal");
        File.WriteAllBytes(temporary, [4, 5, 6]);
        File.WriteAllBytes(destination, [1, 2, 3]);
        var checkpoint = typeof(WriteAheadLog).GetNestedType("Checkpoint", BindingFlags.NonPublic)!;
        var method = checkpoint.GetMethod("PublishFile", BindingFlags.NonPublic | BindingFlags.Static)!;
        var error = Throws<TargetInvocationException>(() => method.Invoke(null, [temporary, destination]));
        IsType<ArgumentException>(error.InnerException);
        Equal(new byte[] { 4, 5, 6 }, File.ReadAllBytes(temporary));
        Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(destination));
    }

    private static DirectoryInfo CreateLocation() => Directory.CreateDirectory(GetTempPath());

    private static byte[] ReadBytes(string path)
    {
        using var handle = File.OpenHandle(path, access: FileAccess.Read, share: FileShare.ReadWrite | FileShare.Delete);
        var result = new byte[checked((int)RandomAccess.GetLength(handle))];
        var position = 0;
        while (position < result.Length)
        {
            var count = RandomAccess.Read(handle, result.AsSpan(position), position);
            True(count > 0);
            position += count;
        }

        return result;
    }

    private static async Task CreateVersion1(DirectoryInfo location)
    {
        using var checkpoint = new CheckpointFile(location);
        await checkpoint.UpdateAsync(0L, 1L, 100UL, 0L, 1L, TestToken);
        await checkpoint.UpdateAsync(1L, 2L, 200UL, 1L, 2L, TestToken);
    }

    private static void WriteIntent(DirectoryInfo location, ReadOnlySpan<byte> primary, long previousGeneration)
    {
        Span<byte> record = stackalloc byte[64];
        record.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(record, 1U);
        BinaryPrimitives.WriteUInt32LittleEndian(record.Slice(4), 0x31504B43U);
        BinaryPrimitives.WriteInt32LittleEndian(record.Slice(8), record.Length);
        primary.Slice(16, 16).CopyTo(record.Slice(16));
        BinaryPrimitives.WriteInt64LittleEndian(record.Slice(32), previousGeneration);
        BinaryPrimitives.WriteInt64LittleEndian(record.Slice(40), previousGeneration + 1L);
        Seal(record);
        File.WriteAllBytes(Path.Combine(location.FullName, "checkpoint.pending"), record);
    }

    private static void Seal(Span<byte> record)
        => BinaryPrimitives.WriteUInt64LittleEndian(record[^8..], Crc64.HashToUInt64(record[..^8]));

    private sealed class CheckpointFile : IDisposable
    {
        private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly Type CheckpointType = typeof(WriteAheadLog).GetNestedType("Checkpoint", BindingFlags.NonPublic)!;
        private static readonly Type Version1Type = typeof(WriteAheadLog).GetNestedType("CheckpointVersion1", BindingFlags.NonPublic)!;
        private readonly object instance;

        internal CheckpointFile(DirectoryInfo location)
        {
            object?[] arguments = [location, null];
            try
            {
                instance = Activator.CreateInstance(CheckpointType, Members, binder: null, arguments, culture: null)!;
                Current = arguments[1];
            }
            catch (TargetInvocationException error) when (error.InnerException is { } cause)
            {
                ExceptionDispatchInfo.Capture(cause).Throw();
                throw;
            }
        }

        internal object? Current { get; }

        internal uint Version => (uint)CheckpointType.GetProperty("Version", Members)!.GetValue(instance)!;

        internal long Generation => (long)CheckpointType.GetProperty("Generation", Members)!.GetValue(instance)!;

        internal T Read<T>(string property)
            => (T)Current!.GetType().GetProperty(property, Members)!.GetValue(Current)!;

        internal ValueTask UpdateAsync(long committed, long appended, ulong position, long snapshot, long generation, CancellationToken token)
        {
            var value = Activator.CreateInstance(Version1Type, [committed, appended, position, snapshot, generation]);
            return (ValueTask)CheckpointType.GetMethod("UpdateAsync", Members)!.Invoke(instance, [value, token])!;
        }

        public void Dispose() => ((IDisposable)instance).Dispose();
    }
}
