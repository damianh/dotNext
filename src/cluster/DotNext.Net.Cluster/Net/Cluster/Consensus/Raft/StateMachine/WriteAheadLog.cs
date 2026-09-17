using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO.Hashing;
using System.Runtime.CompilerServices;
using static System.Threading.Timeout;

namespace DotNext.Net.Cluster.Consensus.Raft.StateMachine;

using Buffers;
using IO;
using IO.Log;
using Threading;
using Threading.Tasks;

/// <summary>
/// Represents the general-purpose Raft WAL.
/// </summary>
/// <remarks>
/// Successful append operations persist their entries and recovery boundary before exposing the new
/// <see cref="LastEntryIndex"/>. This applies even when automatic committed checkpoints are disabled.
/// Recovery retains uncommitted appended entries without applying them. <see cref="CommitAsync"/>
/// advances logical commitment; <see cref="FlushAsync"/> persists its captured committed boundary.
/// Opening legacy stores preserves their known committed history; the first durable update upgrades
/// the checkpoint format, after which older versions cannot open the store.
/// Cancellation or validation failure before an append starts modifying the log leaves the WAL usable.
/// A failed append that may have partially modified the log requires reopening the WAL for recovery.
/// </remarks>
public partial class WriteAheadLog : Disposable, IAsyncDisposable, IPersistentState
{
    private const int DictionaryConcurrencyLevel = 3; // append flow and cleaner and applier

    private readonly NonCryptographicHashAlgorithm? hash;
    private readonly MemoryAllocator<byte> bufferAllocator;
    private readonly IStateMachine stateMachine;
    private readonly CancellationToken lifetimeToken;
    private readonly CancellationTokenMultiplexer cancellationTokens;

    // lifetime management
    [SuppressMessage("Usage", "CA2213", Justification = "False positive")]
    private CancellationTokenSource? lifetimeTokenSource;

    /// <summary>
    /// Initializes a new WAL.
    /// </summary>
    /// <remarks>
    /// If <paramref name="stateMachine"/> supports snapshot restoration (e.g. <see cref="SimpleStateMachine"/>),
    /// its restore method (such as <see cref="SimpleStateMachine.RestoreAsync(System.Threading.CancellationToken)"/>)
    /// must be called, and awaited to completion, before calling this constructor. The constructor reads the
    /// state machine's most recently restored snapshot synchronously to compute the starting point for log
    /// replay; restoring the snapshot afterward has no effect on an already-constructed WAL and forces
    /// a full replay of the log from the beginning.
    /// </remarks>
    /// <param name="configuration">The configuration of the write-ahead log.</param>
    /// <param name="stateMachine">The state machine.</param>
    /// <exception cref="InvalidDataException">
    /// An existing data page file does not match the configured chunk size.
    /// </exception>
    public WriteAheadLog(Options configuration, IStateMachine stateMachine)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(stateMachine);

        // Snapshot getter may throw if the state machine is not restored or initialized
        var snapshotIndex = stateMachine.Snapshot?.Index ?? 0L;
        hash = configuration.CreateHashAlgorithm();
        lifetimeToken = (lifetimeTokenSource = new()).Token;
        backgroundTaskFailureToken = (backgroundTaskFailureSource = new()).Token;
        cancellationTokens = new();
        var rootPath = new DirectoryInfo(configuration.Location);
        rootPath.CreateIfNeeded();
        dataLocation = rootPath.GetSubdirectory(PagedBufferWriter.LocationPrefix);
        PageManager.ValidatePageSize(dataLocation, configuration.ChunkSize);

        context = new(DictionaryConcurrencyLevel, configuration.ConcurrencyLevel);
        lockManager = new()
        {
            ConcurrencyLevel = configuration.ConcurrencyLevel,
            MeasurementTags = configuration.MeasurementTags,
        };
        bufferAllocator = configuration.Allocator ?? ArrayPool<byte>.Shared.ToAllocator();
        this.stateMachine = stateMachine;
        stateLock = new()
        {
            ConcurrencyLevel = configuration.ConcurrencyLevel,
            MeasurementTags = configuration.MeasurementTags,
        };
        state = new(rootPath);
        measurementTags = configuration.MeasurementTags;

        // checkpoint
        long lastReliablyWrittenEntryIndex;
        checkpoint = new(rootPath, out var version);
        switch (version)
        {
            case CheckpointVersion0 cp:
                lastReliablyWrittenEntryIndex = cp.Checkpoint;
                durableState = new(cp.Checkpoint, cp.Checkpoint, 0UL, 0L, 0L);
                break;
            case CheckpointVersion1 cp:
                durableState = cp;
                lastReliablyWrittenEntryIndex = cp.Checkpoint;
                break;
            default:
                checkpoint.Dispose();
                throw new UnsupportedCheckpointVersionException(checkpoint.Version);
        }
        
        (stateMachine as NoOpStateMachine)?.SetLastCommittedIndex(lastReliablyWrittenEntryIndex, durableState.SnapshotIndex);
        snapshotIndex = stateMachine.Snapshot?.Index ?? 0L;
        overwriteJournal = new(rootPath);
        
        // page management
        {
            metadataLocation = rootPath.GetSubdirectory(MetadataPageManager.LocationPrefix);
            metadataLocation.CreateIfNeeded();

            dataLocation.CreateIfNeeded();

            PageManager m, d;
            switch (configuration.MemoryManagement)
            {
                case MemoryManagementStrategy.PrivateMemory when OperatingSystem.IsWindows() && configuration.NoBuffering:
                    m = new WindowsDirectPageManager(metadataLocation, int.Max(Page.MinSize, Environment.SystemPageSize));
                    d = new WindowsDirectPageManager(dataLocation, configuration.ChunkSize);
                    break;
                case MemoryManagementStrategy.PrivateMemory when OperatingSystem.IsLinux() && configuration.NoBuffering:
                    m = new LinuxDirectPageManager(metadataLocation, int.Max(Page.MinSize, Environment.SystemPageSize));
                    d = new LinuxDirectPageManager(dataLocation, configuration.ChunkSize);
                    break;
                case MemoryManagementStrategy.PrivateMemory:
                    m = new AnonymousPageManager(metadataLocation, int.Max(Page.MinSize, Environment.SystemPageSize));
                    d = new AnonymousPageManager(dataLocation, configuration.ChunkSize);
                    break;
                case MemoryManagementStrategy.SharedMemory:
                default:
                    m = new MemoryMappedPageManager(metadataLocation, int.Max(Page.MinSize, Environment.SystemPageSize));
                    d = new MemoryMappedPageManager(dataLocation, configuration.ChunkSize);
                    break;
            }
            
            metadataPages = new(m, hash?.HashLengthInBytes ?? 0);
            overwriteJournal.Recover(durableState, metadataPages);
            var writePosition = version is CheckpointVersion1
                ? durableState.WritePosition
                : metadataPages.TryGetMetadata(durableState.LastIndex, out var metadata) ? metadata.End : 0UL;
            dataPages = new(d)
            {
                LastWrittenAddress = writePosition,
            };
            durableState = durableState with { WritePosition = writePosition };
            // Index zero has no metadata record, but a nonempty snapshot boundary must retain one.
            if (durableState.LastIndex > 0L && durableState.LastIndex >= snapshotIndex)
            {
                if (!metadataPages.TryGetMetadata(durableState.LastIndex, out var tail)
                    || tail.Length < 0L || tail.End < tail.Offset || tail.End != writePosition)
                    throw new InvalidDataException("The durable WAL tail does not match its checkpoint.");
            }
            if (snapshotIndex > durableState.LastIndex)
                WriteSnapshotBoundary(snapshotIndex, stateMachine.Snapshot!.Term);
        }
        
        LastEntryIndex = long.Max(durableState.LastIndex, snapshotIndex);
        LastCommittedEntryIndex = long.Max(lastReliablyWrittenEntryIndex, snapshotIndex);
        applyTrigger = new();
        appliedEvent = new()
        {
            ConcurrencyLevel = configuration.ConcurrencyLevel,
            MeasurementTags = configuration.MeasurementTags,
        };
        
        // flusher
        {
            var interval = configuration.FlushInterval;
            nextUnflushedIndex = commitIndex + 1L;
            flusherOldSnapshot = snapshotIndex;
            if (interval == TimeSpan.Zero)
            {
                flushTrigger = new(initialState: false);
                flushCompleted = new();
                flusherTask = FlushAsync(new BackgroundTrigger(flushTrigger, flushCompleted), lifetimeToken);
            }
            else if (interval == InfiniteTimeSpan)
            {
                foregroundFlushLock = new();
                flusherTask = Task.CompletedTask;
            }
            else
            {
                flushCompleted = new();
                flusherTask = FlushAsync(new TimeoutTrigger(interval, flushCompleted), lifetimeToken);
            }
        }

        // applier
        {
            appliedIndex = snapshotIndex;
            appenderTask = ApplyAsync(lifetimeTokenSource.Token);
        }
    }

    /// <inheritdoc/>
    int IPersistentState.Version => stateMachine.Version;

    /// <inheritdoc/>
    bool IAuditTrail.IsLogEntryLengthAlwaysPresented => true;

    private long SnapshotIndex => stateMachine.Snapshot?.Index ?? 0L;

    /// <summary>
    /// Initializes the log asynchronously.
    /// </summary>
    /// <remarks>
    /// The default implementation applies committed log entries to the underlying state machine.
    /// </remarks>
    /// <param name="token">The token that can be used to cancel the operation.</param>
    /// <returns></returns>
    public virtual async Task InitializeAsync(CancellationToken token = default)
    {
        ObjectDisposedException.ThrowIf(IsDisposingOrDisposed, this);
        ThrowOnInternalError();
        await lockManager.AcquireAppendLockAsync(token).ConfigureAwait(false);
        try
        {
            await lockManager.UpgradeToOverwriteLockAsync(token).ConfigureAwait(false);
            VerifyIntegrity(token);
        }
        finally
        {
            lockManager.ReleaseAppendLock();
        }
        
        await WaitForApplyAsync(LastCommittedEntryIndex, token).ConfigureAwait(false);
    }

    private void VerifyIntegrity(CancellationToken token)
    {
        // skip snapshot from verification
        for (var index = SnapshotIndex + 1L; index <= LastEntryIndex; index++, token.ThrowIfCancellationRequested())
        {
            var reader = metadataPages.GetView<MetadataReader>(index);
            var metadata = reader.Metadata;
            if (metadata.Length < 0L || metadata.Offset > durableState.WritePosition
                || (ulong)metadata.Length > durableState.WritePosition - metadata.Offset)
                throw new InvalidDataException("A WAL entry extends beyond the durable data boundary.");
            dataPages.ValidateRange(metadata.Offset, metadata.Length);
            if (hash is not null)
            {
                dataPages.ComputeHash(hash, metadata.Offset, metadata.Length);
                reader.CompleteAndVerifyHash(hash);
            }
        }
    }

    /// <inheritdoc cref="IAuditTrail.LastEntryIndex"/>
    public long LastEntryIndex
    {
        get => Atomic.Read(in field);
        private set => Atomic.Write(ref field, value);
    }

    private async ValueTask<long> AppendUnbufferedAsync<TEntry>(TEntry entry, CancellationToken token)
        where TEntry : IRaftLogEntry
    {
        long currentIndex;
        lockManager.SetCallerInformation("Append Single Entry");
        await lockManager.AcquireAppendLockAsync(token).ConfigureAwait(false);
        try
        {
            currentIndex = LastEntryIndex + 1L;
            await persistenceLock.AcquireAsync(token).ConfigureAwait(false);
            var mutationStarted = false;
            try
            {
                ThrowOnInternalError();
                token.ThrowIfCancellationRequested();
                var length = entry.Length;
                token.ThrowIfCancellationRequested();
                mutationStarted = true;
                await PrepareAppendAsync(currentIndex, token).ConfigureAwait(false);
                await AppendAsync(entry, length, out var startAddress, token).ConfigureAwait(false);
                WriteMetadata(entry, currentIndex, startAddress);
                await PersistAppendAsync(currentIndex, token).ConfigureAwait(false);
            }
            catch (Exception e) when (mutationStarted)
            {
                OnBackgroundTaskFailure(e);
                throw;
            }
            finally
            {
                persistenceLock.Release();
            }
        }
        finally
        {
            lockManager.ReleaseAppendLock();
        }

        return currentIndex;
    }
    
    private async ValueTask<long> AppendBufferedAsync<TEntry>(TEntry entry, CancellationToken token)
        where TEntry : struct, IBufferedLogEntry
    {
        lockManager.SetCallerInformation("Append Single Buffered Entry");
        try
        {
            await lockManager.AcquireAppendLockAsync(token).ConfigureAwait(false);
            try
            {
                await persistenceLock.AcquireAsync(token).ConfigureAwait(false);
                var mutationStarted = false;
                try
                {
                    ThrowOnInternalError();
                    token.ThrowIfCancellationRequested();
                    mutationStarted = true;
                    await PrepareAppendAsync(LastEntryIndex + 1L, token).ConfigureAwait(false);
                    var index = AppendBuffered(entry);
                    await PersistAppendAsync(index, token).ConfigureAwait(false);
                    return index;
                }
                catch (Exception e) when (mutationStarted)
                {
                    OnBackgroundTaskFailure(e);
                    throw;
                }
                finally
                {
                    persistenceLock.Release();
                }
            }
            finally
            {
                lockManager.ReleaseAppendLock();
            }
        }
        finally
        {
            if (typeof(TEntry) == typeof(BufferedLogEntry))
                Unsafe.As<TEntry, BufferedLogEntry>(ref entry).Dispose();
        }
    }

    private long AppendBuffered<TEntry>(TEntry entry)
        where TEntry : struct, IBufferedLogEntry
    {
        var currentIndex = stagedLastIndex + 1L;
        var startAddress = dataPages.LastWrittenAddress;
        dataPages.Write(entry.Content);
        WriteMetadata(entry, currentIndex, startAddress);
        return currentIndex;
    }

    /// <inheritdoc cref="IAuditTrail{TEntryImpl}.AppendAsync{TEntry}(TEntry, CancellationToken)"/>
    public ValueTask<long> AppendAsync<TEntry>(TEntry entry, CancellationToken token = default)
        where TEntry : IRaftLogEntry
    {
        ValueTask<long> task;
        if (IsDisposingOrDisposed)
        {
            task = new(GetDisposedTask<long>());
        }
        else if (backgroundTaskFailure is { } exception)
        {
            task = ValueTask.FromException<long>(new InternalException(exception));
        }
        else if (typeof(TEntry) == typeof(BinaryLogEntry))
        {
            task = AppendBufferedAsync(Unsafe.As<TEntry, BinaryLogEntry>(ref entry), token);
        }
        else if (entry.IsSnapshot)
        {
            task = ValueTask.FromException<long>(new InvalidOperationException(ExceptionMessages.SnapshotDetected));
        }
        else if (entry.TryGetMemory(out var payload))
        {
            var entryCopy = new BinaryLogEntry
            {
                IsConfiguration = entry.IsConfiguration,
                Term = entry.Term,
                Content = payload,
                CommandId = entry.CommandId,
                Context = entry is IInputLogEntry { Context: { } ctx } ? ctx : null,
            };

            task = AppendBufferedAsync(entryCopy, token);
        }
        else if (entry is ISupplier<MemoryAllocator<byte>, MemoryOwner<byte>>)
        {
            // make a copy out of the lock
            var entryCopy = new BufferedLogEntry(((ISupplier<MemoryAllocator<byte>, MemoryOwner<byte>>)entry).Invoke(bufferAllocator))
            {
                IsConfiguration = entry.IsConfiguration,
                Term = entry.Term,
                CommandId = entry.CommandId,
                Context = entry is IInputLogEntry { Context: { } ctx } ? ctx : null,
            };

            task = AppendBufferedAsync(entryCopy, token);
        }
        else
        {
            task = AppendUnbufferedAsync(entry, token);
        }

        return task;
    }

    /// <inheritdoc cref="IAuditTrail{TEntryImpl}.AppendAsync{TEntry}(TEntry, long, CancellationToken)"/>
    public async ValueTask AppendAsync<TEntry>(TEntry entry, long startIndex, CancellationToken token = default)
        where TEntry : IRaftLogEntry
    {
        ArgumentOutOfRangeException.ThrowIfNegative(startIndex);
        ObjectDisposedException.ThrowIf(IsDisposingOrDisposed, this);
        ThrowOnInternalError();

        lockManager.SetCallerInformation("Append Single Entry at Custom Index");
        await lockManager.AcquireAppendLockAsync(token).ConfigureAwait(false);
        try
        {
            var tailIndex = LastEntryIndex;
            if (entry.IsSnapshot)
            {
                lockManager.SetCallerInformation("Install Snapshot");
                await lockManager.UpgradeToOverwriteLockAsync(token).ConfigureAwait(false);
                if (startIndex <= LastCommittedEntryIndex)
                    throw new InvalidOperationException(ExceptionMessages.InvalidAppendIndex);
                
                await persistenceLock.AcquireAsync(token).ConfigureAwait(false);
                var mutationStarted = false;
                try
                {
                    ThrowOnInternalError();
                    token.ThrowIfCancellationRequested();
                    var snapshot = new LogEntry(entry, startIndex);
                    token.ThrowIfCancellationRequested();
                    mutationStarted = true;
                    LastAppliedIndex = await stateMachine.ApplyAsync(snapshot, token).ConfigureAwait(false);
                    var snapshotIndex = stateMachine.Snapshot?.Index ?? startIndex;
                    if (snapshotIndex > tailIndex)
                        WriteSnapshotBoundary(snapshotIndex, entry.Term);

                    LastCommittedEntryIndex = long.Max(LastCommittedEntryIndex, snapshotIndex);
                    stagedLastIndex = long.Max(tailIndex, LastCommittedEntryIndex);
                    await PersistAppendAsync(snapshotIndex, token).ConfigureAwait(false);
                    OnSnapshotInstalled(snapshotIndex);
                }
                catch (Exception e) when (mutationStarted)
                {
                    OnBackgroundTaskFailure(e);
                    throw;
                }
                finally
                {
                    persistenceLock.Release();
                }
            }
            else
            {
                switch (startIndex.CompareTo(++tailIndex))
                {
                    case > 0:
                        throw new ArgumentOutOfRangeException(nameof(startIndex));
                    case < 0:
                        lockManager.SetCallerInformation("Overwrite Uncommitted Tail");
                        await lockManager.UpgradeToOverwriteLockAsync(token).ConfigureAwait(false);
                        if (startIndex <= LastCommittedEntryIndex)
                            throw new InvalidOperationException(ExceptionMessages.InvalidAppendIndex);
                        break;
                }

                await persistenceLock.AcquireAsync(token).ConfigureAwait(false);
                var mutationStarted = false;
                try
                {
                    ThrowOnInternalError();
                    token.ThrowIfCancellationRequested();
                    var length = entry.Length;
                    token.ThrowIfCancellationRequested();
                    mutationStarted = true;
                    await PrepareAppendAsync(startIndex, token).ConfigureAwait(false);
                    await AppendAsync(entry, length, out var startAddress, token).ConfigureAwait(false);
                    WriteMetadata(entry, startIndex, startAddress);
                    await PersistAppendAsync(startIndex, token).ConfigureAwait(false);
                }
                catch (Exception e) when (mutationStarted)
                {
                    OnBackgroundTaskFailure(e);
                    throw;
                }
                finally
                {
                    persistenceLock.Release();
                }
            }
        }
        finally
        {
            lockManager.ReleaseAppendLock();
        }
    }

    /// <inheritdoc cref="IAuditTrail{TEntryImpl}.AppendAsync{TEntry}(ILogEntryProducer{TEntry}, long, bool, CancellationToken)"/>
    public async ValueTask AppendAsync<TEntry>(ILogEntryProducer<TEntry> entries, long startIndex, bool skipCommitted = false,
        CancellationToken token = default)
        where TEntry : IRaftLogEntry
    {
        ArgumentOutOfRangeException.ThrowIfNegative(startIndex);
        ObjectDisposedException.ThrowIf(IsDisposingOrDisposed, this);
        ThrowOnInternalError();

        lockManager.SetCallerInformation("Append Multiple Entries");
        await lockManager.AcquireAppendLockAsync(token).ConfigureAwait(false);
        try
        {
            switch (startIndex.CompareTo(LastEntryIndex + 1L))
            {
                case > 0:
                    throw new ArgumentOutOfRangeException(nameof(startIndex));
                case < 0:
                    lockManager.SetCallerInformation("Overwrite Uncommitted Tail");
                    await lockManager.UpgradeToOverwriteLockAsync(token).ConfigureAwait(false);
                    break;
            }
            
            await AppendCoreAsync(entries, startIndex, skipCommitted, token).ConfigureAwait(false);
        }
        finally
        {
            lockManager.ReleaseAppendLock();
        }
    }

    private async ValueTask AppendCoreAsync<TEntry>(ILogEntryProducer<TEntry> entries, long startIndex, bool skipCommitted,
        CancellationToken token, bool preserveMatching = false)
        where TEntry : IRaftLogEntry
    {
        await persistenceLock.AcquireAsync(token).ConfigureAwait(false);
        var mutationStarted = false;
        try
        {
            ThrowOnInternalError();
            token.ThrowIfCancellationRequested();
            var commitIndex = LastCommittedEntryIndex;
            var firstIndex = long.Max(startIndex, commitIndex + 1L);
            for (; await entries.MoveNextAsync().ConfigureAwait(false); startIndex++)
            {
                token.ThrowIfCancellationRequested();
                if (entries.Current is not { IsSnapshot: false } currentEntry)
                    throw new InvalidOperationException(ExceptionMessages.SnapshotDetected);

                if (startIndex > commitIndex)
                {
                    if (preserveMatching && startIndex <= LastEntryIndex
                        && metadataPages.GetView<MetadataReader>(startIndex).Metadata.Term == currentEntry.Term)
                        continue;

                    preserveMatching = false;
                    var length = currentEntry.Length;
                    if (!mutationStarted)
                    {
                        // Producer validation and skipped entries must not create an overwrite journal.
                        token.ThrowIfCancellationRequested();
                        mutationStarted = true;
                        firstIndex = startIndex;
                        await PrepareAppendAsync(firstIndex, token).ConfigureAwait(false);
                    }

                    await AppendAsync(currentEntry, length, out var startAddress, token).ConfigureAwait(false);
                    WriteMetadata(currentEntry, startIndex, startAddress);
                }
                else if (!skipCommitted)
                {
                    throw new InvalidOperationException(ExceptionMessages.InvalidAppendIndex);
                }
            }

            token.ThrowIfCancellationRequested();
            if (mutationStarted)
                await PersistAppendAsync(firstIndex, token).ConfigureAwait(false);
        }
        catch (Exception e) when (mutationStarted)
        {
            OnBackgroundTaskFailure(e);
            throw;
        }
        finally
        {
            persistenceLock.Release();
        }
    }

    /// <inheritdoc/>
    ValueTask<long> IAuditTrail<IRaftLogEntry>.AppendAndCommitAsync<TEntry>(ILogEntryProducer<TEntry> entries, long startIndex, bool skipCommitted,
        long commitIndex, CancellationToken token)
    {
        return IsDisposingOrDisposed
            ? new(GetDisposedTask<long>())
            : backgroundTaskFailure is { } exception
                ? ValueTask.FromException<long>(new InternalException(exception))
                : entries.RemainingCount is 0L
                    ? CommitAsync(commitIndex, token)
                    : commitIndex < startIndex
                        ? AppendAndCommitAsync(entries, startIndex, skipCommitted, commitIndex, token)
                        : AppendAndCommitSlowAsync(entries, startIndex, skipCommitted, commitIndex, token);
    }

    private async ValueTask<long> AppendAndCommitAsync<TEntry>(ILogEntryProducer<TEntry> entries, long startIndex, bool skipCommitted,
        long commitIndex, CancellationToken token)
        where TEntry : IRaftLogEntry
    {
        // the best case for this method - raise flusher and applier in parallel with the appending process
        var committedCount = await CommitCoreAsync<FalseConstant>(commitIndex, token).ConfigureAwait(false);
        var delayedPostCommit = committedCount > 0L;
        var appendLockTaken = false;

        try
        {
            lockManager.SetCallerInformation("Append and Commit");
            await lockManager.AcquireAppendLockAsync(token).ConfigureAwait(false);
            appendLockTaken = true;
            switch (startIndex.CompareTo(LastEntryIndex + 1L))
            {
                case > 0:
                    throw new ArgumentOutOfRangeException(nameof(startIndex));
                case < 0:
                    // Defer notification until the overwrite lock no longer suspends the workers.
                    lockManager.SetCallerInformation("Overwrite Uncommitted Tail");
                    await lockManager.UpgradeToOverwriteLockAsync(token).ConfigureAwait(false);
                    break;
                case 0 when delayedPostCommit:
                    delayedPostCommit = false;
                    OnCommitted(committedCount);
                    break;
            }

            await AppendCoreAsync(entries, startIndex, skipCommitted, token, preserveMatching: true).ConfigureAwait(false);
        }
        finally
        {
            if (appendLockTaken)
                lockManager.ReleaseAppendLock();

            // Commitment is independent of appending, including cancellation while acquiring its locks.
            if (delayedPostCommit)
                OnCommitted(committedCount);
        }

        return committedCount;
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<long> AppendAndCommitSlowAsync<TEntry>(ILogEntryProducer<TEntry> entries, long startIndex, bool skipCommitted,
        long commitIndex, CancellationToken token)
        where TEntry : IRaftLogEntry
    {
        lockManager.SetCallerInformation("Append and Commit");
        await lockManager.AcquireAppendLockAsync(token).ConfigureAwait(false);
        try
        {
            if (startIndex > LastEntryIndex + 1L)
                throw new ArgumentOutOfRangeException(nameof(startIndex));
            if (startIndex <= LastEntryIndex)
                await lockManager.UpgradeToOverwriteLockAsync(token).ConfigureAwait(false);

            await AppendCoreAsync(entries, startIndex, skipCommitted, token, preserveMatching: true).ConfigureAwait(false);
        }
        finally
        {
            lockManager.ReleaseAppendLock();
        }

        return await CommitAsync(commitIndex, token).ConfigureAwait(false);
    }

    private ValueTask AppendAsync<TEntry>(TEntry entry, long? length, out ulong startAddress, CancellationToken token)
        where TEntry : IRaftLogEntry
    {
        var hasCapacity = dataPages.TryEnsureCapacity(length);
        startAddress = dataPages.LastWrittenAddress;

        return hasCapacity
            ? DataTransferObject.WriteToAsync(entry, dataPages, token)
            : entry.WriteToAsync(dataPages, token);
    }

    private void WriteMetadata<TEntry>(TEntry entry, long index, ulong startAddress)
        where TEntry : IRaftLogEntry
    {
        var length = long.CreateChecked(dataPages.LastWrittenAddress - startAddress);
        var writer = metadataPages.GetView<MetadataWriter>(index);
        writer.WriteMetadata(LogEntryMetadata.Create(entry, startAddress, length));

        if (hash is not null)
        {
            dataPages.ComputeHash(hash, startAddress, length);
            length += writer.CompleteAndWriteHash(hash);
        }

        if (entry is IInputLogEntry { Context: { } ctx })
        {
            context[index] = ctx;
        }

        stagedLastIndex = index;
        AppendRateMeter.Add(1L, measurementTags);
        BytesWrittenMeter.Record(length + LogEntryMetadata.Size, measurementTags);
    }

    // An installed snapshot squashes every index below it, so those indices have no ordinary metadata and their
    // pages may never have been allocated. Materializing a payload-free record at the snapshot index keeps the
    // flushing, checkpointing, recovery and page reclamation paths working on a real metadata entry.
    private void WriteSnapshotBoundary(long index, long term)
    {
        var writer = metadataPages.GetView<MetadataWriter>(index);
        writer.WriteMetadata(LogEntryMetadata.CreateSnapshotBoundary(term, dataPages.LastWrittenAddress));

        if (hash is not null)
            writer.CompleteAndWriteHash(hash);
    }

    /// <inheritdoc cref="IAuditTrail.CommitAsync(long, CancellationToken)"/>
    public ValueTask<long> CommitAsync(long endIndex, CancellationToken token = default)
    {
        ValueTask<long> task;
        if (endIndex < 0L || endIndex > LastEntryIndex)
        {
            task = ValueTask.FromException<long>(new ArgumentOutOfRangeException(nameof(endIndex)));
        }
        else if (IsDisposingOrDisposed)
        {
            task = new(GetDisposedTask<long>());
        }
        else if (backgroundTaskFailure is { } exception)
        {
            task = ValueTask.FromException<long>(new InternalException(exception));
        }
        else
        {
            task = CommitCoreAsync<TrueConstant>(endIndex, token);
        }

        return task;
    }

    private ValueTask<long> CommitCoreAsync<TNotify>(long endIndex, CancellationToken token)
        where TNotify : struct, IConstant<bool>
    {
        ValueTask<long> task;

        if (lockManager.TryAcquireCommitLock())
        {
            long count;
            try
            {
                count = Commit(endIndex);
            }
            finally
            {
                lockManager.ReleaseCommitLock();
            }
            
            // notify out of the lock
            try
            {
                if (TNotify.Value && count > 0L)
                    OnCommitted(count);

                task = new(count);
            }
            catch (Exception e)
            {
                task = ValueTask.FromException<long>(e);
            }
        }
        else
        {
            task = CommitSlowAsync<TNotify>(endIndex, token);
        }

        return task;
    }

    private async ValueTask<long> CommitSlowAsync<TNotify>(long endIndex, CancellationToken token)
        where TNotify : struct, IConstant<bool>
    {
        lockManager.SetCallerInformation("Commit");
        await lockManager.AcquireCommitLockAsync(token).ConfigureAwait(false);
        long count;
        try
        {
            count = Commit(endIndex);
        }
        finally
        {
            lockManager.ReleaseCommitLock();
        }

        if (TNotify.Value && count > 0L)
            OnCommitted(count);

        return count;
    }

    /// <inheritdoc cref="IAuditTrail.WaitForApplyAsync(CancellationToken)"/>
    public ValueTask WaitForApplyAsync(CancellationToken token = default)
        => backgroundTaskFailure is { } exception
            ? ValueTask.FromException(new InternalException(exception))
            : appliedEvent.WaitAsync(token);

    /// <inheritdoc cref="IAuditTrail.WaitForApplyAsync(long, CancellationToken)"/>
    public ValueTask WaitForApplyAsync(long index, CancellationToken token = default)
        => backgroundTaskFailure is { } exception
            ? ValueTask.FromException(new InternalException(exception))
            : appliedEvent.SpinWaitAsync<CommitChecker>(new(this, index), token);

    /// <inheritdoc cref="IAuditTrail{TEntryImpl}.ReadAsync{TResult}(ILogEntryConsumer{TEntryImpl, TResult}, long, long, CancellationToken)"/>
    public ValueTask<TResult> ReadAsync<TResult>(ILogEntryConsumer<IRaftLogEntry, TResult> reader, long startIndex, long endIndex,
        CancellationToken token = default)
    {
        ValueTask<TResult> task;
        if (IsDisposingOrDisposed)
            task = new(GetDisposedTask<TResult>());
        else if (startIndex < 0L)
            task = ValueTask.FromException<TResult>(new ArgumentOutOfRangeException(nameof(startIndex)));
        else if (endIndex < 0L || endIndex > LastEntryIndex)
            task = ValueTask.FromException<TResult>(new ArgumentOutOfRangeException(nameof(endIndex)));
        else if (backgroundTaskFailure is { } exception)
            task = ValueTask.FromException<TResult>(new InternalException(exception));
        else if (startIndex > endIndex)
            task = reader.ReadAsync<LogEntry, LogEntry[]>([], null, token);
        else
            task = ReadCoreAsync(reader, startIndex, endIndex, token);

        return task;
    }

    private async ValueTask<TResult> ReadCoreAsync<TResult>(ILogEntryConsumer<IRaftLogEntry, TResult> reader, long startIndex, long endIndex,
        CancellationToken token = default)
    {
        lockManager.SetCallerInformation("Read Entries");
        await lockManager.AcquireReadLockAsync(token).ConfigureAwait(false);
        try
        {
            ThrowOnInternalError();
            ArgumentOutOfRangeException.ThrowIfGreaterThan(endIndex, LastEntryIndex);
            var list = new LogEntryList(
                stateMachine,
                startIndex,
                endIndex,
                reader.LogEntryMetadataOnly ? null : dataPages,
                metadataPages,
                out var snapshotIndex);
            return await reader.ReadAsync<LogEntry, LogEntryList>(list, snapshotIndex, token).ConfigureAwait(false);
        }
        finally
        {
            lockManager.ReleaseReadLock();
        }
    }

    private void CleanUp()
    {
        metadataPages.Dispose();
        dataPages.Dispose();
        Dispose<QueuedSynchronizer>(lockManager, appliedEvent, stateLock);
        flushCompleted?.Dispose();
        persistenceLock.Dispose();
        checkpoint.Dispose();
        state.Dispose();
        context.Clear();
        backgroundTaskFailureSource.Dispose();
        backgroundTaskFailure = null;
    }

    private void CancelBackgroundJobs()
    {
        if (Interlocked.Exchange(ref lifetimeTokenSource, null) is { } cts)
        {
            using (cts)
            {
                cts.Cancel();
            }
        }
        
        flushTrigger?.Set();
        applyTrigger.Set();
    }

    /// <inheritdoc/>
    protected override async ValueTask DisposeAsyncCore()
    {
        CancelBackgroundJobs();
        
        await flusherTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        if (foregroundFlushLock is not null)
            await foregroundFlushLock.DisposeAsync().ConfigureAwait(false);

        await appenderTask.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

        if (cleanupTask.TryGetTarget(out var task))
            await task.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        
        CleanUp();
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            DisposeAsyncCore().Wait();
        }

        base.Dispose(disposing);
    }

    /// <inheritdoc cref="IAsyncDisposable.DisposeAsync()"/>
    public new ValueTask DisposeAsync() => base.DisposeAsync();
}

file static class DirectoryInfoExtensions
{
    public static void CreateIfNeeded(this DirectoryInfo directory)
    {
        if (!directory.Exists)
            directory.Create();
    }

    public static DirectoryInfo GetSubdirectory(this DirectoryInfo root, string prefix)
        => new(Path.Combine(root.FullName, prefix));
}