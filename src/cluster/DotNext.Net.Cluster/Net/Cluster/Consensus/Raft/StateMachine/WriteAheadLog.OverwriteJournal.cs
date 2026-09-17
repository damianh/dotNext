using System.Buffers.Binary;
using System.IO.Hashing;

namespace DotNext.Net.Cluster.Consensus.Raft.StateMachine;

partial class WriteAheadLog
{
    private sealed class OverwriteJournal(DirectoryInfo location)
    {
        private const int HeaderSize = 32;
        private readonly string path = Path.Combine(location.FullName, "overwrite");

        internal async ValueTask WriteAsync(long generation, long first, long last,
            MetadataPageManager pages, CancellationToken token)
        {
            var temporary = path + ".tmp";
            var header = new byte[HeaderSize];
            BinaryPrimitives.WriteInt64LittleEndian(header, generation);
            BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(8), first);
            BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(16), last - first + 1L);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(24), pages.GetRecord(first).Length);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(28), 1);
            var hash = new Crc64();
            hash.Append(header);
            var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough);
            await using (output.ConfigureAwait(false))
            {
                await output.WriteAsync(header, token).ConfigureAwait(false);
                for (var index = first; index <= last; index++)
                {
                    var record = pages.GetRecord(index);
                    hash.Append(record.Span);
                    await output.WriteAsync(record, token).ConfigureAwait(false);
                }

                var checksum = new byte[sizeof(ulong)];
                BinaryPrimitives.WriteUInt64LittleEndian(checksum, hash.GetCurrentHashAsUInt64());
                await output.WriteAsync(checksum, token).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }

            Checkpoint.PublishFile(temporary, path);
        }

        internal void Recover(CheckpointVersion1 checkpoint, MetadataPageManager pages)
        {
            if (!File.Exists(path))
                return;

            using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Span<byte> header = stackalloc byte[HeaderSize];
                input.ReadExactly(header);
                var generation = BinaryPrimitives.ReadInt64LittleEndian(header);
                var first = BinaryPrimitives.ReadInt64LittleEndian(header[8..]);
                var count = BinaryPrimitives.ReadInt64LittleEndian(header[16..]);
                var size = BinaryPrimitives.ReadInt32LittleEndian(header[24..]);
                if (BinaryPrimitives.ReadInt32LittleEndian(header[28..]) != 1
                    || generation <= 0L || first <= 0L || count <= 0L
                    || size <= 0 || size > Environment.SystemPageSize
                    || input.Length != checked(HeaderSize + count * size + sizeof(ulong)))
                    throw new InvalidDataException("Invalid WAL overwrite journal.");

                var buffer = new byte[size];
                var hash = new Crc64();
                hash.Append(header);
                for (var index = 0L; index < count; index++)
                {
                    input.ReadExactly(buffer);
                    hash.Append(buffer);
                }

                Span<byte> checksum = stackalloc byte[sizeof(ulong)];
                input.ReadExactly(checksum);
                if (hash.GetCurrentHashAsUInt64() != BinaryPrimitives.ReadUInt64LittleEndian(checksum))
                    throw new InvalidDataException("Invalid WAL overwrite journal checksum.");

                if (generation > checkpoint.Generation)
                {
                    var last = checked(first + count - 1L);
                    if (generation != checkpoint.Generation + 1L || first <= checkpoint.Checkpoint
                        || last != checkpoint.LastIndex)
                        throw new InvalidDataException("The overwrite journal does not match the WAL checkpoint.");

                    input.Position = HeaderSize;
                    for (var index = first; index <= last; index++)
                    {
                        input.ReadExactly(buffer);
                        pages.RestoreRecord(index, buffer);
                    }

                    pages.FlushAsync(first, last, CancellationToken.None).AsTask().GetAwaiter().GetResult();
                }
            }

            Clear();
        }

        internal void Clear()
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                Checkpoint.FlushDirectory(location);
            }
        }
    }
}
