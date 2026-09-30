namespace DotNext.Net.Cluster.Consensus.Raft.StateMachine;

using IO;

/// <summary>
/// A <see cref="SimpleStateMachine"/> whose entire state is the content of its latest snapshot.
/// </summary>
internal sealed class ByteStateMachine(DirectoryInfo location) : SimpleStateMachine(location)
{
    internal byte[] State { get; private set; } = [];

    /// <summary>
    /// Runs inside <c>RestoreAsync</c>, before the state is replaced, with the token the state machine passes to it.
    /// </summary>
    internal Func<CancellationToken, ValueTask> BeforeRestore { get; set; }

    protected override async ValueTask RestoreAsync(FileInfo snapshotFile, CancellationToken token)
    {
        if (BeforeRestore is { } hook)
            await hook(token);

        State = await File.ReadAllBytesAsync(snapshotFile.FullName, token);
    }

    /// <summary>
    /// Runs inside <c>PersistAsync</c>, which is the background snapshotting of the state.
    /// </summary>
    internal Func<CancellationToken, ValueTask> BeforePersist { get; set; }

    /// <summary>
    /// Makes applying a regular entry start a background snapshot.
    /// </summary>
    internal bool SnapshotOnApply { get; set; }

    protected override async ValueTask PersistAsync(IAsyncBinaryWriter writer, CancellationToken token)
    {
        if (BeforePersist is { } hook)
            await hook(token);

        await writer.Invoke(State, token);
    }

    protected override ValueTask<bool> ApplyAsync(LogEntry entry, CancellationToken token)
        => ValueTask.FromResult(SnapshotOnApply);
}

/// <summary>
/// Lets the test act at the moment the WAL hands a snapshot to the state machine.
/// </summary>
internal sealed class SnapshotApplyProbe(ByteStateMachine machine, Action onSnapshotApply) : IStateMachine
{
    public bool IsSnapshotInstallCancellationSafe => machine.As<IStateMachine>().IsSnapshotInstallCancellationSafe;

    public ISnapshot Snapshot => machine.As<IStateMachine>().Snapshot;

    public ValueTask<long> ApplyAsync(LogEntry entry, CancellationToken token)
    {
        if (entry.IsSnapshot)
            onSnapshotApply();

        return machine.As<IStateMachine>().ApplyAsync(entry, token);
    }

    public ValueTask ReclaimGarbageAsync(long watermark, CancellationToken token)
        => machine.As<IStateMachine>().ReclaimGarbageAsync(watermark, token);
}

/// <summary>
/// An incoming snapshot that writes half of its content, then lets the test interfere with the transfer.
/// </summary>
internal sealed class ByteSnapshotEntry(byte[] content, long term, Func<CancellationToken, ValueTask> midTransfer = null)
    : IRaftLogEntry
{
    public long Term => term;

    public bool IsSnapshot => true;

    public long? Length => content.LongLength;

    public bool IsReusable => true;

    public async ValueTask WriteToAsync<TWriter>(TWriter writer, CancellationToken token)
        where TWriter : IAsyncBinaryWriter
    {
        var half = content.Length / 2;
        await writer.WriteAsync(content.AsMemory(0, half), null, token);
        if (midTransfer is not null)
            await midTransfer(token);

        await writer.WriteAsync(content.AsMemory(half), null, token);
    }
}
