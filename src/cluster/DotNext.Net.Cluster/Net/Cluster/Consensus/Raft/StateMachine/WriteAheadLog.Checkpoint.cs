using System.Buffers.Binary;
using System.IO.Hashing;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DotNext.Net.Cluster.Consensus.Raft.StateMachine;

using Buffers;
using Buffers.Binary;
using IO.Log;

partial class WriteAheadLog
{
    private sealed partial class Checkpoint : IDisposable
    {
        private const int VersionLength = sizeof(uint);
        private const string FileName = "checkpoint";
        private const string PreparedFileName = "checkpoint.prepared";
        private const string PendingFileName = "checkpoint.pending";
        private const string FormatFileName = "checkpoint.format";

        // Header and slots occupy separate 64 KiB regions, not adjacent sectors of
        // one filesystem page. No write assumes a whole region is atomic.
        private const int BlockSize = 64 * 1024;
        private const int FileSize = BlockSize * 3;
        private const int SmallRecordSize = 64;
        private const uint HeaderMagic = 0x31484B43;
        private const uint SlotMagic = 0x31534B43;
        private const uint PendingMagic = 0x31504B43;
        private const uint FormatMagic = 0x31464B43;

        private readonly DirectoryInfo location;
        private readonly string path;
        private readonly byte[] buffer = GC.AllocateArray<byte>(BlockSize, pinned: true);
        private SafeFileHandle? handle;
        private Guid identity;
        private bool formatPublished;

        internal uint Version { get; private set; }

        internal long Generation { get; private set; }

        internal Checkpoint(DirectoryInfo location, out IVersionedCheckpoint? checkpoint)
        {
            this.location = location;
            path = Path.Combine(location.FullName, FileName);

            var formatPath = Path.Combine(location.FullName, FormatFileName);
            var pendingPath = Path.Combine(location.FullName, PendingFileName);
            var format = ReadOptionalRecord(formatPath, FormatMagic);
            var pending = ReadOptionalRecord(pendingPath, PendingMagic);

            try
            {
                try
                {
                    handle = Open();
                }
                catch (FileNotFoundException)
                {
                    // Do not turn a missing checkpoint in an existing WAL into a
                    // successful recovery of an empty log.
                    if (format is not null || pending is not null
                        || ContainsPages("metadata") || ContainsPages("data"))
                    {
                        throw new IntegrityException("The WAL checkpoint is missing.");
                    }

                    using (var initial = File.OpenHandle(path, FileMode.CreateNew, FileAccess.Write))
                        FlushFile(initial);

                    FlushDirectory(location);
                    if (location.Parent is { } parent)
                        FlushDirectory(parent);
                    handle = Open();
                }

                var length = RandomAccess.GetLength(handle);
                if (format is not null && length != FileSize)
                    throw new IntegrityException("The version-1 WAL checkpoint is truncated.");

                switch (length)
                {
                    case 0L when format is null && pending is null:
                        Version = CheckpointVersion0.Version;
                        checkpoint = new CheckpointVersion0(0L);
                        break;
                    case sizeof(long) when format is null && pending is null:
                        ReadExactly(handle, buffer.AsSpan(0, sizeof(long)), 0L);
                        if (BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(4)) == HeaderMagic)
                            throw new IntegrityException("The WAL checkpoint header is truncated.");
                        Version = CheckpointVersion0.Version;
                        checkpoint = ValidateLegacy(BinaryPrimitives.ReadInt64LittleEndian(buffer));
                        break;
                    case < VersionLength:
                        throw new IntegrityException("The WAL checkpoint header is truncated.");
                    default:
                        ReadExactly(handle, buffer.AsSpan(0, VersionLength), 0L);
                        Version = BinaryPrimitives.ReadUInt32LittleEndian(buffer);
                        switch (Version)
                        {
                            case CheckpointVersion0.Version when length == VersionLength + CheckpointVersion0.Size
                                                                 && format is null && pending is null:
                                ReadExactly(handle, buffer.AsSpan(0, CheckpointVersion0.Size), VersionLength);
                                checkpoint = ValidateLegacy(CheckpointVersion0.Parse(buffer).Checkpoint);
                                break;
                            case CheckpointVersion0.Version:
                                throw new IntegrityException("Invalid version-0 WAL checkpoint length or upgrade state.");
                            case CheckpointVersion1.Version when length == FileSize:
                                var current = ReadVersion1(format, pending);
                                Generation = current.Generation;
                                checkpoint = current;
                                break;
                            case CheckpointVersion1.Version:
                                throw new IntegrityException("Invalid version-1 WAL checkpoint length.");
                            default:
                                checkpoint = null;
                                break;
                        }

                        break;
                }
            }
            catch
            {
                handle?.Dispose();
                throw;
            }
        }

        private bool ContainsPages(string directory)
        {
            var directoryPath = Path.Combine(location.FullName, directory);
            return Directory.Exists(directoryPath) && Directory.EnumerateFileSystemEntries(directoryPath).Any();
        }

        private SafeFileHandle Open()
            => File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite,
                FileShare.Read | FileShare.Delete, FileOptions.Asynchronous | FileOptions.WriteThrough);

        private static CheckpointVersion0 ValidateLegacy(long checkpoint)
            => checkpoint >= 0L
                ? new(checkpoint)
                : throw new IntegrityException("The legacy WAL checkpoint index is negative.");

        private CheckpointVersion1 ReadVersion1(byte[]? format, byte[]? pending)
        {
            ReadExactly(handle!, buffer, 0L);
            if (!ValidateRecord(buffer, HeaderMagic)
                || BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(12)) != BlockSize
                || buffer.AsSpan(32, BlockSize - 40).ContainsAnyExcept((byte)0))
            {
                throw new IntegrityException("Invalid WAL checkpoint header.");
            }

            identity = new Guid(buffer.AsSpan(16, 16));
            if (identity == Guid.Empty)
                throw new IntegrityException("The WAL checkpoint identity is empty.");

            if (format is not null)
            {
                ValidateIdentity(format);
                if (format.AsSpan(32, 24).ContainsAnyExcept((byte)0))
                    throw new IntegrityException("Invalid WAL checkpoint format marker.");
                formatPublished = true;
            }

            var first = ReadSlot(0);
            var second = ReadSlot(1);
            if (pending is not null)
            {
                ValidateIdentity(pending);
                var previousGeneration = BinaryPrimitives.ReadInt64LittleEndian(pending.AsSpan(32));
                var nextGeneration = BinaryPrimitives.ReadInt64LittleEndian(pending.AsSpan(40));
                if (previousGeneration < 1L || previousGeneration == long.MaxValue
                    || nextGeneration != previousGeneration + 1L
                    || pending.AsSpan(48, 8).ContainsAnyExcept((byte)0))
                {
                    throw new IntegrityException("Invalid WAL checkpoint publication intent.");
                }

                var previous = (previousGeneration & 1L) == 0L ? first : second;
                var next = (nextGeneration & 1L) == 0L ? first : second;
                if (previous is not { } stable || stable.Generation != previousGeneration)
                    throw new IntegrityException("The preceding WAL checkpoint generation is damaged.");

                // Only the slot named by a durably published intent may be torn.
                // A completed slot without such an intent must never be silently
                // discarded: doing so could discard acknowledged log entries.
                return next switch
                {
                    { } completed when completed.Generation == nextGeneration => completed,
                    null => stable,
                    { } old when old.Generation == long.Max(1L, previousGeneration - 1L) => stable,
                    _ => throw new IntegrityException("The WAL checkpoint publication generations do not match."),
                };
            }

            if (first is not { } a || second is not { } b)
                throw new IntegrityException("A completed WAL checkpoint generation is damaged.");

            // The first publication initializes both slots, so an all-zero slot
            // cannot later masquerade as an unused slot after corruption.
            if (a.Generation == 1L && a == b)
                return a;
            if ((a.Generation & 1L) != 0L || (b.Generation & 1L) != 1L
                || long.Max(a.Generation, b.Generation) - long.Min(a.Generation, b.Generation) != 1L)
            {
                throw new IntegrityException("Invalid WAL checkpoint generation sequence.");
            }

            return a.Generation > b.Generation ? a : b;
        }

        private CheckpointVersion1? ReadSlot(int slot)
        {
            ReadExactly(handle!, buffer, SlotOffset(slot));
            if (!buffer.AsSpan().ContainsAnyExcept((byte)0) || !HasValidChecksum(buffer))
                return null;

            if (!ValidateRecord(buffer, SlotMagic)
                || BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(12)) != 0U
                || buffer.AsSpan(72, BlockSize - 80).ContainsAnyExcept((byte)0)
                || new Guid(buffer.AsSpan(16, 16)) != identity)
            {
                throw new IntegrityException("Invalid WAL checkpoint generation record.");
            }

            var checkpoint = new CheckpointVersion1(
                BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(32)),
                BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(40)),
                BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(48)),
                BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(56)),
                BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(64)));
            Validate(checkpoint);
            return checkpoint;
        }

        private static void Validate(CheckpointVersion1 checkpoint)
        {
            if (checkpoint.Checkpoint < 0L || checkpoint.LastIndex < checkpoint.Checkpoint
                || checkpoint.SnapshotIndex < 0L || checkpoint.SnapshotIndex > checkpoint.Checkpoint
                || checkpoint.LastIndex == 0L && checkpoint.WritePosition != 0UL
                || checkpoint.Generation < 1L)
            {
                throw new IntegrityException("Invalid WAL checkpoint boundaries.");
            }
        }

        private void ValidateIdentity(ReadOnlySpan<byte> record)
        {
            if (new Guid(record.Slice(16, 16)) != identity)
                throw new IntegrityException("The WAL checkpoint sidecar belongs to a different store.");
        }

        private static long SlotOffset(int slot) => (slot + 1L) * BlockSize;

        public async ValueTask UpdateAsync(CheckpointVersion1 checkpoint, CancellationToken token)
        {
            ObjectDisposedException.ThrowIf(handle is null || handle.IsClosed, this);
            Validate(checkpoint);
            if (Generation == long.MaxValue || checkpoint.Generation != Generation + 1L)
                throw new ArgumentOutOfRangeException(nameof(checkpoint), "Checkpoint generations must be consecutive.");

            token.ThrowIfCancellationRequested();
            if (Version == CheckpointVersion0.Version)
            {
                await UpgradeAsync(checkpoint, token).ConfigureAwait(false);
            }
            else if (Version == CheckpointVersion1.Version)
            {
                // A recovered upgrade may have stopped between primary
                // publication and its permanent format marker.
                PublishFormat();
                var pending = CreateSmallRecord(PendingMagic);
                BinaryPrimitives.WriteInt64LittleEndian(pending.AsSpan(32), Generation);
                BinaryPrimitives.WriteInt64LittleEndian(pending.AsSpan(40), checkpoint.Generation);
                Seal(pending);
                PublishRecord(PendingFileName, pending);

                FormatSlot(checkpoint);
                await RandomAccess.WriteAsync(handle!, buffer,
                    SlotOffset((int)(checkpoint.Generation & 1L)), token).ConfigureAwait(false);
                FlushFile(handle!);

                // Removing the intent, including its directory entry, certifies
                // both slots as complete. Recovery permits fallback only while
                // the intent remains, never for an acknowledged torn slot.
                File.Delete(Path.Combine(location.FullName, PendingFileName));
                FlushDirectory(location);
                if (OperatingSystem.IsMacOS())
                    FlushFile(handle!);
            }
            else
            {
                throw new UnsupportedCheckpointVersionException(Version);
            }

            Generation = checkpoint.Generation;
        }

        private async ValueTask UpgradeAsync(CheckpointVersion1 checkpoint, CancellationToken token)
        {
            identity = Guid.NewGuid();
            var preparedPath = Path.Combine(location.FullName, PreparedFileName);
            using (var prepared = File.OpenHandle(preparedPath, FileMode.Create, FileAccess.Write,
                       FileShare.None, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                InitializeRecord(buffer, HeaderMagic);
                BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(12), BlockSize);
                Seal(buffer);
                await RandomAccess.WriteAsync(prepared, buffer, 0L, token).ConfigureAwait(false);
                FormatSlot(checkpoint);
                await RandomAccess.WriteAsync(prepared, buffer, SlotOffset(0), token).ConfigureAwait(false);
                await RandomAccess.WriteAsync(prepared, buffer, SlotOffset(1), token).ConfigureAwait(false);
            }

            // The legacy primary is untouched until a complete replacement is
            // durable. The published primary starts with version 1, so old
            // binaries reject it instead of opening a legacy-looking sidecar.
            handle!.Dispose();
            try
            {
                PublishFile(preparedPath, path);
            }
            finally
            {
                handle = Open();
            }
            Version = CheckpointVersion1.Version;
            PublishFormat();
        }

        private void PublishFormat()
        {
            if (formatPublished)
                return;

            var format = CreateSmallRecord(FormatMagic);
            Seal(format);
            PublishRecord(FormatFileName, format);
            formatPublished = true;
        }

        private byte[] CreateSmallRecord(uint magic)
        {
            var record = new byte[SmallRecordSize];
            InitializeRecord(record, magic);
            return record;
        }

        private void InitializeRecord(Span<byte> record, uint magic)
        {
            record.Clear();
            BinaryPrimitives.WriteUInt32LittleEndian(record, CheckpointVersion1.Version);
            BinaryPrimitives.WriteUInt32LittleEndian(record.Slice(4), magic);
            BinaryPrimitives.WriteInt32LittleEndian(record.Slice(8), record.Length);
            identity.TryWriteBytes(record.Slice(16, 16));
        }

        private void FormatSlot(CheckpointVersion1 checkpoint)
        {
            InitializeRecord(buffer, SlotMagic);
            BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(32), checkpoint.Checkpoint);
            BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(40), checkpoint.LastIndex);
            BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(48), checkpoint.WritePosition);
            BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(56), checkpoint.SnapshotIndex);
            BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(64), checkpoint.Generation);
            Seal(buffer);
        }

        private void PublishRecord(string fileName, ReadOnlySpan<byte> record)
        {
            var destination = Path.Combine(location.FullName, fileName);
            var prepared = destination + ".prepared";
            using (var file = File.OpenHandle(prepared, FileMode.Create, FileAccess.Write))
            {
                RandomAccess.Write(file, record, 0L);
            }

            PublishFile(prepared, destination);
        }

        private static byte[]? ReadOptionalRecord(string path, uint magic)
        {
            SafeFileHandle file;
            try
            {
                file = File.OpenHandle(path);
            }
            catch (FileNotFoundException)
            {
                return null;
            }

            using (file)
            {
                if (RandomAccess.GetLength(file) != SmallRecordSize)
                    throw new IntegrityException("Invalid WAL checkpoint sidecar length.");
                var record = new byte[SmallRecordSize];
                ReadExactly(file, record, 0L);
                if (!ValidateRecord(record, magic)
                    || BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(12)) != 0U)
                {
                    throw new IntegrityException("Invalid WAL checkpoint sidecar.");
                }

                return record;
            }
        }

        private static void ReadExactly(SafeFileHandle file, Span<byte> destination, long offset)
        {
            while (!destination.IsEmpty)
            {
                var count = RandomAccess.Read(file, destination, offset);
                if (count == 0)
                    throw new IntegrityException("The WAL checkpoint record is truncated.");
                destination = destination.Slice(count);
                offset += count;
            }
        }

        private static bool ValidateRecord(ReadOnlySpan<byte> record, uint magic)
        {
            if (!HasValidChecksum(record))
                return false;

            var version = BinaryPrimitives.ReadUInt32LittleEndian(record);
            if (version != CheckpointVersion1.Version)
                throw new UnsupportedCheckpointVersionException(version);

            return BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(4)) == magic
                   && BinaryPrimitives.ReadInt32LittleEndian(record.Slice(8)) == record.Length;
        }

        private static bool HasValidChecksum(ReadOnlySpan<byte> record)
            => BinaryPrimitives.ReadUInt64LittleEndian(record[^sizeof(ulong)..])
               == Crc64.HashToUInt64(record[..^sizeof(ulong)]);

        private static void Seal(Span<byte> record)
            => BinaryPrimitives.WriteUInt64LittleEndian(record[^sizeof(ulong)..],
                Crc64.HashToUInt64(record[..^sizeof(ulong)]));

        internal static void FlushFile(SafeFileHandle file)
            => DurableFile.Flush(file);

        internal static void PublishFile(string temporaryPath, string destinationPath)
            => DurableFile.Publish(temporaryPath, destinationPath);

        // On macOS, follow a namespace barrier with FlushFile on an open WAL
        // file to push the directory update through the device's write cache.
        internal static void FlushDirectory(DirectoryInfo directory)
            => DurableFile.FlushDirectory(directory);

        public void Dispose() => handle?.Dispose();
    }
    
    private interface IVersionedCheckpoint
    {
        long Checkpoint { get; }
        
        static abstract uint Version { get; }
    }

    private readonly record struct CheckpointVersion1(long Checkpoint, long LastIndex, ulong WritePosition, long SnapshotIndex, long Generation)
        : IVersionedCheckpoint
    {
        public const uint Version = 1;

        static uint IVersionedCheckpoint.Version => Version;
    }

    [StructLayout(LayoutKind.Auto)]
    private readonly struct CheckpointVersion0(long checkpoint) : IBinaryFormattable<CheckpointVersion0>, IVersionedCheckpoint
    {
        public const uint Version = 0;
        public const int Size = sizeof(long);

        static int IBinaryFormattable<CheckpointVersion0>.Size => Size;
        
        public void Format(scoped Span<byte> destination)
        {
            var writer = new SpanWriter<byte>(destination);
            writer.WriteLittleEndian(checkpoint);
        }

        public static CheckpointVersion0 Parse(scoped ReadOnlySpan<byte> source)
        {
            var reader = new SpanReader<byte>(source);
            return new(reader.ReadLittleEndian<long>());
        }

        public long Checkpoint => checkpoint;

        static uint IVersionedCheckpoint.Version => Version;
    }
    
    /// <summary>
    /// Indicates that the checkpoint file has unsupported version.
    /// </summary>
    public sealed class UnsupportedCheckpointVersionException : IntegrityException
    {
        internal UnsupportedCheckpointVersionException(uint actualVersion)
            : base(ExceptionMessages.BadCheckpointVersion(actualVersion))
            => Version = actualVersion;
        
        /// <summary>
        /// Gets the actual version that is not supported.
        /// </summary>
        [CLSCompliant(false)]
        public uint Version { get; }
    }
}