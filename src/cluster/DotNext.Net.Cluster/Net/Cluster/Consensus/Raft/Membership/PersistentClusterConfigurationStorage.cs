using System.Diagnostics.CodeAnalysis;
using Microsoft.Win32.SafeHandles;
using static System.Buffers.Binary.BinaryPrimitives;

namespace DotNext.Net.Cluster.Consensus.Raft.Membership;

using Buffers;
using IO;
using IO.Log;
using StateMachine;

/// <summary>
/// Represents persistent cluster configuration storage.
/// </summary>
/// <typeparam name="TAddress">The type of the cluster member address.</typeparam>
public abstract class PersistentClusterConfigurationStorage<TAddress> : ClusterConfigurationStorage<TAddress>
    where TAddress : notnull
{
    private const FileOptions Options = FileOptions.Asynchronous | FileOptions.SequentialScan;
    
    private readonly string configurationFile;
    private readonly Action<DirectoryInfo> flushDirectory;
    private readonly Func<SafeFileHandle, Memory<byte>, long, CancellationToken, ValueTask<int>> readAsync;
    private volatile bool publicationPending;

    /// <summary>
    /// Initializes a new persistent storage.
    /// </summary>
    /// <param name="fileName">The full path to the file used as persistent storage of cluster members.</param>
    protected PersistentClusterConfigurationStorage(string fileName)
        : this(fileName, DurableFile.FlushDirectory, RandomAccess.ReadAsync)
    {
    }

    private protected PersistentClusterConfigurationStorage(string fileName, Action<DirectoryInfo> flushDirectory)
        : this(fileName, flushDirectory, RandomAccess.ReadAsync)
    {
    }

    private protected PersistentClusterConfigurationStorage(string fileName, Action<DirectoryInfo> flushDirectory,
        Func<SafeFileHandle, Memory<byte>, long, CancellationToken, ValueTask<int>> readAsync)
    {
        configurationFile = fileName;
        ArgumentNullException.ThrowIfNull(flushDirectory);
        this.flushDirectory = flushDirectory;
        ArgumentNullException.ThrowIfNull(readAsync);
        this.readAsync = readAsync;

        var file = new FileInfo(fileName);
        DeleteStaleTemporaryFiles(file);
        if (file.Exists)
        {
            DurableFile.FlushPublication(file, flushDirectory);
        }
    }

    /// <inheritdoc/>
    protected sealed override async ValueTask<(MemoryOwner<byte> Configuration, long Version)> LoadConfigurationAsync(CancellationToken token)
    {
        CompletePendingPublication();

        if (!File.Exists(configurationFile))
            return default;

        using var handle = File.OpenHandle(configurationFile, FileMode.Open, FileAccess.Read, FileShare.Read, Options);

        var length = int.CreateChecked(RandomAccess.GetLength(handle));
        ThrowIfTruncated(length);

        var versionBuffer = MemoryAllocator.AllocateExactly(sizeof(long));
        try
        {
            var configBuffer = MemoryAllocator.AllocateExactly(length - sizeof(long));
            if (!await ReadExactlyAsync(handle, versionBuffer.Memory, fileOffset: 0L, token).ConfigureAwait(false)
                || !await ReadExactlyAsync(handle, configBuffer.Memory, fileOffset: sizeof(long), token).ConfigureAwait(false))
            {
                configBuffer.Dispose();
                ThrowIfTruncated(RandomAccess.GetLength(handle));
                throw new IntegrityException($"The cluster configuration file '{configurationFile}' changed while it was being read.");
            }

            return (configBuffer, ReadInt64LittleEndian(versionBuffer.Span));
        }
        finally
        {
            versionBuffer.Dispose();
        }
    }

    /// <inheritdoc/>
    protected sealed override ValueTask<bool> SaveConfigurationAsync(ReadOnlyMemory<byte> configuration, long configurationVersion,
        CancellationToken token)
    {
        CompletePendingPublication();
        return File.Exists(configurationFile)
            ? RewriteConfigurationAsync(configuration, configurationVersion, token)
            : SaveFreshConfigurationAsync(configuration, configurationVersion, token);
    }

    private async ValueTask<bool> SaveFreshConfigurationAsync(ReadOnlyMemory<byte> configuration, long configurationVersion,
        CancellationToken token)
    {
        var versionBuffer = MemoryAllocator.AllocateExactly(sizeof(long));
        try
        {
            WriteInt64LittleEndian(versionBuffer.Span, configurationVersion);
            await WriteAndPublishAsync(versionBuffer.Memory, configuration, token).ConfigureAwait(false);
        }
        finally
        {
            versionBuffer.Dispose();
        }
        
        return true;
    }

    private async ValueTask<bool> RewriteConfigurationAsync(ReadOnlyMemory<byte> configuration, long configurationVersion,
        CancellationToken token)
    {
        var versionBuffer = MemoryAllocator.AllocateExactly(sizeof(long));
        try
        {
            long version;

            // restore version from file
            using (var handle = File.OpenHandle(configurationFile, FileMode.Open, FileAccess.Read, FileShare.Read, Options))
            {
                var length = RandomAccess.GetLength(handle);
                ThrowIfTruncated(length);
                if (!await ReadExactlyAsync(handle, versionBuffer.Memory, fileOffset: 0L, token).ConfigureAwait(false))
                {
                    ThrowIfTruncated(RandomAccess.GetLength(handle));
                    throw new IntegrityException($"The cluster configuration file '{configurationFile}' changed while it was being read.");
                }

                version = ReadInt64LittleEndian(versionBuffer.Span);
            }

            if (configurationVersion <= version)
                return false;

            WriteInt64LittleEndian(versionBuffer.Span, configurationVersion);
            await WriteAndPublishAsync(versionBuffer.Memory, configuration, token).ConfigureAwait(false);
        }
        finally
        {
            versionBuffer.Dispose();
        }

        return true;
    }

    private async ValueTask WriteAndPublishAsync(ReadOnlyMemory<byte> version, ReadOnlyMemory<byte> configuration,
        CancellationToken token)
    {
        var tempFile = string.Concat(configurationFile, ".", Path.GetRandomFileName(), ".tmp");
        try
        {
            using (var handle = File.OpenHandle(tempFile,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       Options,
                       configuration.Length + sizeof(long)))
            {
                File.SetAttributes(handle, FileAttributes.NotContentIndexed);
                await RandomAccess
                    .WriteAsync(handle, [version, configuration], fileOffset: 0L, token)
                    .ConfigureAwait(false);
            }

            publicationPending = true;
            try
            {
                DurableFile.Publish(tempFile, configurationFile, flushDirectory);
                publicationPending = false;
            }
            catch
            {
                if (File.Exists(tempFile))
                    publicationPending = false;

                throw;
            }
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    private async ValueTask<bool> ReadExactlyAsync(SafeFileHandle handle, Memory<byte> output, long fileOffset,
        CancellationToken token)
    {
        while (!output.IsEmpty)
        {
            var count = await readAsync(handle, output, fileOffset, token).ConfigureAwait(false);
            if (count is 0)
                return false;

            output = output[count..];
            fileOffset += count;
        }

        return true;
    }

    private void CompletePendingPublication()
    {
        if (publicationPending)
        {
            DurableFile.FlushPublication(new FileInfo(configurationFile), flushDirectory);
            publicationPending = false;
        }
    }

    private void ThrowIfTruncated(long length)
    {
        if (length < sizeof(long))
            ThrowTruncated(length);
    }

    [DoesNotReturn]
    private void ThrowTruncated(long length)
        => throw new IntegrityException(
            $"The cluster configuration file '{configurationFile}' is truncated ({length} of at least {sizeof(long)} header bytes). " +
            "The WAL may no longer contain configuration entries covered by a snapshot. Restore the file from a backup, " +
            "or remove this member from the cluster and re-add it with an empty WAL directory. Deleting the file can restore " +
            "an obsolete configuration and is unsafe.");

    private static void DeleteStaleTemporaryFiles(FileInfo configurationFile)
    {
        if (configurationFile.Directory is not { Exists: true } directory)
            return;

        var prefix = string.Concat(configurationFile.Name, ".");
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        foreach (var path in Directory.EnumerateFiles(directory.FullName))
        {
            var name = Path.GetFileName(path);
            if (!name.StartsWith(prefix, comparison) || !name.EndsWith(".tmp", comparison))
                continue;

            try
            {
                File.Delete(path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Stale files are harmless and can be retried on the next reopen.
            }
        }
    }
}