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
/// after the in-memory term/vote were already updated and before any byte reaches the file. This models a
/// transient storage error. Switch the handle only while the WAL is quiescent.
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

    internal NodeStateFault(WriteAheadLog log, string location)
    {
        this.log = log;
        original = (SafeFileHandle)HandleField.GetValue(StateField.GetValue(log));
        readOnly = File.OpenHandle(Path.Combine(location, "state"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    }

    internal void Break() => Install(readOnly);

    internal void Restore() => Install(original);

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
        original.Dispose();
    }
}
