using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace DotNext.Net.Cluster.Consensus.Raft.StateMachine;

using IO.Log;

partial class WriteAheadLog
{
    private readonly CancellationTokenSource backgroundTaskFailureSource;
    private readonly CancellationToken backgroundTaskFailureToken;
    private volatile Exception? backgroundTaskFailure;

    private void OnBackgroundTaskFailure(Exception e)
    {
        var failure = Interlocked.CompareExchange(ref backgroundTaskFailure, e, null) ?? e;
        backgroundTaskFailureSource.Cancel();
        flushTrigger?.Set();
        applyTrigger.Set();
        flushCompleted?.Signal(resumeAll: true);
        appliedEvent.Interrupt(new InternalException(failure));
    }

    private void ThrowOnInternalError()
    {
        if (backgroundTaskFailure is { } exception)
            ThrowInternalException(exception);
        
        [DoesNotReturn]
        [StackTraceHidden]
        static void ThrowInternalException(Exception e) => throw new InternalException(e);
    }
    
    /// <summary>
    /// Indicates internal WAL exception.
    /// </summary>
    public sealed class InternalException : IntegrityException
    {
        internal InternalException(Exception innerException)
            : base(innerException.Message, innerException)
        {
        }
    }
    
    /// <summary>
    /// Indicates that the hash of the log entry doesn't match.
    /// </summary>
    public sealed class HashMismatchException : IntegrityException
    {
        internal HashMismatchException()
            : base(ExceptionMessages.LogEntryHashMismatch)
        {
            
        }
    }
    
    /// <summary>
    /// Indicates that the log doesn't have a page on the disk.
    /// </summary>
    public sealed class MissingPageException : IntegrityException
    {
        internal MissingPageException(uint pageIndex)
            : base(ExceptionMessages.MissingWalPage(pageIndex))
        {
        }
    }
}