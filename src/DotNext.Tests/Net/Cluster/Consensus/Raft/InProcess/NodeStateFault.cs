using System.Reflection;
using Microsoft.Win32.SafeHandles;

namespace DotNext.Net.Cluster.Consensus.Raft.InProcess;

using StateMachine;

/// <summary>
/// Makes writes of the WAL term/vote record fail without touching the file's contents.
/// </summary>
/// <remarks>
/// <c>WriteAheadLog.NodeState</c> is a private struct, so the tests reach its handle by reflection.
/// While broken, the record is written through a read-only handle to the same file: the write call fails
/// before any byte reaches the file. This models a transient storage error. <see cref="WriteRecord"/> stores a record
/// through the original handle to model a failed write that reached the file anyway. <see cref="BreakReadBack"/> also makes
/// the read-back after a failed write fail: the record is written to and read from a read-only handle to an empty file.
/// Switch the handle only while the WAL is quiescent.
/// </remarks>
internal sealed class NodeStateFault : IDisposable
{
    private static readonly FieldInfo StateField = typeof(WriteAheadLog)
        .GetField("state", BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly FieldInfo HandleField = StateField.FieldType
        .GetField("handle", BindingFlags.Instance | BindingFlags.NonPublic);

    private readonly WriteAheadLog log;
    private readonly SafeFileHandle original;
    private readonly SafeFileHandle readOnly;
    private readonly string emptyPath;
    private readonly SafeFileHandle unreadable;

    internal NodeStateFault(WriteAheadLog log, string location)
    {
        this.log = log;
        original = (SafeFileHandle)HandleField.GetValue(StateField.GetValue(log));
        readOnly = File.OpenHandle(Path.Combine(location, "state"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        emptyPath = Path.GetTempFileName();
        unreadable = File.OpenHandle(emptyPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    }

    internal void Break() => Install(readOnly);

    /// <summary>
    /// Like <see cref="Break"/>, but reading the record back fails too, so the WAL cannot learn what is on disk.
    /// </summary>
    internal void BreakReadBack() => Install(unreadable);

    internal void Restore() => Install(original);

    /// <summary>
    /// Writes the record through the original handle, as if a write reported as failed had reached the file.
    /// </summary>
    internal void WriteRecord(ReadOnlySpan<byte> record) => RandomAccess.Write(original, record, fileOffset: 0L);

    private void Install(SafeFileHandle handle)
    {
        var boxed = StateField.GetValue(log);
        HandleField.SetValue(boxed, handle);
        StateField.SetValue(log, boxed);
    }

    /// <summary>
    /// Releases both handles, so the file can be reopened by a replacement WAL.
    /// </summary>
    public void Dispose()
    {
        readOnly.Dispose();
        unreadable.Dispose();
        original.Dispose();
        File.Delete(emptyPath);
    }
}
