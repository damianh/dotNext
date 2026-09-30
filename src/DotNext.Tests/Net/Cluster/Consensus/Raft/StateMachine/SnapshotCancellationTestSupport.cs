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

    protected override ValueTask PersistAsync(IAsyncBinaryWriter writer, CancellationToken token)
        => writer.Invoke(State, token);

    protected override ValueTask<bool> ApplyAsync(LogEntry entry, CancellationToken token)
        => ValueTask.FromResult(false);
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
