using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace DotNext.Threading;

using Patterns;

/// <summary>
/// Provides low-level infrastructure for writing custom synchronization primitives.
/// </summary>
/// <typeparam name="TContext">The context to be associated with each suspended caller.</typeparam>
public abstract class QueuedSynchronizer<TContext> : QueuedSynchronizer
{
    private new sealed class WaitNode :
        QueuedSynchronizer.WaitNode,
        IWaitNodeFeature<TContext>
    {
        internal TContext? Context;

        protected override void CleanUp()
        {
            if (RuntimeHelpers.IsReferenceOrContainsReferences<TContext>())
                Context = default;

            base.CleanUp();
        }

        TContext IWaitNodeFeature<TContext>.Feature => Context!;
    }

    // The distinct contexts of the suspended callers that a drain of the queue has passed.
    // When it is full, the drain stops, as if overtaking were not allowed.
    [StructLayout(LayoutKind.Auto)]
    private struct SuspendedContexts
    {
        private const int Capacity = 8;

        private Buffer buffer;
        private int count;

        public readonly bool IsEmpty => count is 0;

        public bool TryAdd(TContext context)
        {
            Span<TContext> contexts = buffer;
            foreach (var other in contexts[..count])
            {
                if (EqualityComparer<TContext>.Default.Equals(other, context))
                    return true;
            }

            if (count is Capacity)
                return false;

            contexts[count++] = context;
            return true;
        }

        [UnscopedRef]
        public readonly ReadOnlySpan<TContext> AsReadOnlySpan()
            => ((ReadOnlySpan<TContext>)buffer)[..count];

        [InlineArray(Capacity)]
        private struct Buffer
        {
            private TContext element;
        }
    }

    // Set by the default implementation of CanOvertake, which keeps the strict order of the queue.
    private bool overtakingDisabled;

    /// <summary>
    /// Initializes a new synchronization primitive.
    /// </summary>
    protected QueuedSynchronizer()
    {
    }

    /// <summary>
    /// Tests whether the lock acquisition can be done successfully before calling <see cref="AcquireCore(TContext)"/>.
    /// </summary>
    /// <param name="context">The context associated with the suspended caller or supplied externally.</param>
    /// <returns><see langword="true"/> if acquisition is allowed; otherwise, <see langword="false"/>.</returns>
    /// <exception cref="Exception">A custom exception is thrown.</exception>
    protected abstract bool CanAcquire(TContext context);

    /// <summary>
    /// Returns an optional exception if <see cref="CanAcquire"/> returns <see langword="false"/>.
    /// </summary>
    /// <param name="context">The acquisition context.</param>
    /// <returns>The exception; or <see langword="null"/>.</returns>
    protected virtual ExceptionFactory? GetAcquisitionException(TContext context) => null;

    /// <summary>
    /// Modifies the internal state according to acquisition semantics.
    /// </summary>
    /// <remarks>
    /// By default, this method does nothing.
    /// </remarks>
    /// <param name="context">The context associated with the suspended caller or supplied externally.</param>
    protected virtual void AcquireCore(TContext context)
    {
    }

    /// <summary>
    /// Modifies the internal state according to release semantics.
    /// </summary>
    /// <remarks>
    /// This method is called by <see cref="Release(TContext)"/> method.
    /// </remarks>
    /// <param name="context">The context associated with the suspended caller or supplied externally.</param>
    protected virtual void ReleaseCore(TContext context)
    {
    }

    private protected sealed override void DrainWaitQueue(ref WaitQueueScope queue)
    {
        var suspended = new SuspendedContexts();
        for (; !queue.IsEndOfQueue<WaitNode, TContext>(out var context); queue.Advance())
        {
            if (suspended.IsEmpty || CanOvertake(context, in suspended))
            {
                switch (CanAcquire(context))
                {
                    case false when GetAcquisitionException(context) is { } factory:
                        queue.SignalCurrent(factory.CreateException());
                        continue;
                    case false:
                        break;
                    case true:
                        if (queue.SignalCurrent())
                            AcquireCore(context);

                        continue;
                }
            }

            // The caller stays suspended. The callers behind it can be resumed only if they cannot delay it.
            if (overtakingDisabled || !suspended.TryAdd(context))
                return;
        }
    }

    private bool CanOvertake(TContext context, ref readonly SuspendedContexts suspended)
    {
        foreach (var other in suspended.AsReadOnlySpan())
        {
            if (!CanOvertake(context, other))
                return false;
        }

        return true;
    }

    private bool CanOvertakeSuspendedCallers(TContext context)
    {
        if (overtakingDisabled)
            return false;

        for (var node = FirstSuspendedCaller; node is not null; node = node.Next)
        {
            if (node is WaitNode { Context: var other } && !CanOvertake(context, other!))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Determines whether a caller can acquire this synchronizer ahead of a suspended caller.
    /// </summary>
    /// <remarks>
    /// By default, the suspended callers are resumed strictly in the order of the queue, and a new caller cannot acquire
    /// this synchronizer while the queue is not empty. An override can let a caller pass the suspended callers
    /// that it cannot delay: a new caller, or a suspended caller when the queue is drained, acquires this synchronizer
    /// if <see cref="CanAcquire(TContext)"/> returns <see langword="true"/> and this method returns
    /// <see langword="true"/> for every suspended caller ahead of it. A caller that is placed at the head of the queue by
    /// <see cref="AcquirePriorityAsync(TContext, CancellationToken)"/> is passed only under the same rule.
    /// To guarantee that every suspended caller eventually acquires this synchronizer, return <see langword="true"/> only if
    /// the acquisition with <paramref name="context"/> cannot cause <see cref="CanAcquire(TContext)"/> to return
    /// <see langword="false"/> for <paramref name="suspended"/>.
    /// The method is called while the internal state is locked, and must not modify the state.
    /// An override must not call the base implementation, which returns <see langword="false"/> and turns off
    /// overtaking for this object.
    /// </remarks>
    /// <param name="context">The context of the caller that acquires this synchronizer.</param>
    /// <param name="suspended">The context of a suspended caller ahead of it.</param>
    /// <returns>
    /// <see langword="true"/> if the caller with <paramref name="context"/> can acquire this synchronizer ahead of
    /// <paramref name="suspended"/>; otherwise, <see langword="false"/>.
    /// </returns>
    protected virtual bool CanOvertake(TContext context, TContext suspended)
    {
        overtakingDisabled = true;
        return false;
    }

    private protected sealed override bool IsReadyToDispose => IsEmptyQueue;

    /// <summary>
    /// Implements release semantics: attempts to resume the suspended callers.
    /// </summary>
    /// <remarks>
    /// This method doesn't invoke <see cref="ReleaseCore(TContext)"/> method and trying to resume
    /// suspended callers.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">This object has been disposed.</exception>
    protected void Release()
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);

        var queue = CaptureWaitQueue();
        try
        {
            DrainWaitQueue(ref queue);

            if (IsDisposing && IsReadyToDispose)
                Dispose(true);
        }
        finally
        {
            queue.Dispose();
        }
    }

    /// <summary>
    /// Implements release semantics: attempts to resume the suspended callers.
    /// </summary>
    /// <remarks>
    /// This method invokes <se cref="ReleaseCore(TContext)"/> to modify the internal state
    /// before resuming all suspended callers.
    /// </remarks>
    /// <param name="context">The argument to be passed to <see cref="ReleaseCore(TContext)"/>.</param>
    /// <exception cref="ObjectDisposedException">This object has been disposed.</exception>
    protected void Release(TContext context)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);

        var queue = CaptureWaitQueue();
        try
        {
            ReleaseCore(context);
            DrainWaitQueue(ref queue);

            if (IsDisposing && IsReadyToDispose)
                Dispose(true);
        }
        finally
        {
            queue.Dispose();
        }
    }

    /// <summary>
    /// Implements acquire semantics: attempts to move this object to acquired state synchronously.
    /// </summary>
    /// <remarks>
    /// This method invokes <see cref="CanAcquire(TContext)"/>, and if it returns <see langword="true"/>,
    /// invokes <see cref="AcquireCore(TContext)"/> to modify the internal state.
    /// </remarks>
    /// <param name="context">The context to be passed to <see cref="CanAcquire(TContext)"/>.</param>
    /// <returns><see langword="true"/> if this primitive is in acquired state; otherwise, <see langword="false"/>.</returns>
    /// <exception cref="ObjectDisposedException">This object has been disposed.</exception>
    protected bool TryAcquire(TContext context)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);

        using (CaptureWaitQueue())
        {
            return TryAcquireCore(context);
        }
    }

    /// <summary>
    /// Implements acquire semantics: attempts to move this object to acquired state asynchronously.
    /// </summary>
    /// <param name="context">The context to be passed to <see cref="CanAcquire(TContext)"/>.</param>
    /// <param name="timeout">The time to wait for the acquisition.</param>
    /// <param name="token">The token that can be used to cancel the operation.</param>
    /// <returns><see langword="true"/> if acquisition is successful; <see langword="false"/> if timeout occurred.</returns>
    /// <exception cref="OperationCanceledException">The operation has been canceled.</exception>
    /// <exception cref="ObjectDisposedException">This object has been disposed.</exception>
    protected ValueTask<bool> TryAcquireAsync(TContext context, TimeSpan timeout, CancellationToken token)
    {
        var builder = BeginAcquisition(timeout, token);
        return AcquireAsync<ValueTask<bool>, TimeoutAndCancellationToken>(context, ref builder);
    }

    /// <summary>
    /// Implements acquire semantics: attempts to move this object to acquired state asynchronously.
    /// </summary>
    /// <param name="context">The context to be passed to <see cref="CanAcquire(TContext)"/>.</param>
    /// <param name="timeout">The time to wait for the acquisition.</param>
    /// <param name="token">The token that can be used to cancel the operation.</param>
    /// <returns><see langword="true"/> if acquisition is successful; <see langword="false"/> if timeout occurred.</returns>
    /// <exception cref="TimeoutException">The operation cannot be completed within the specified amount of time.</exception>
    /// <exception cref="OperationCanceledException">The operation has been canceled.</exception>
    /// <exception cref="ObjectDisposedException">This object has been disposed.</exception>
    protected ValueTask AcquireAsync(TContext context, TimeSpan timeout, CancellationToken token)
    {
        var builder = BeginAcquisition(timeout, token);
        return AcquireAsync<ValueTask, TimeoutAndCancellationToken>(context, ref builder);
    }

    /// <summary>
    /// Implements acquire semantics: attempts to move this object to acquired state asynchronously.
    /// </summary>
    /// <param name="context">The context to be passed to <see cref="CanAcquire(TContext)"/>.</param>
    /// <param name="token">The token that can be used to cancel the operation.</param>
    /// <returns>The task representing asynchronous execution of this method.</returns>
    /// <exception cref="OperationCanceledException">The operation has been canceled.</exception>
    /// <exception cref="ObjectDisposedException">This object has been disposed.</exception>
    protected ValueTask AcquireAsync(TContext context, CancellationToken token)
    {
        var builder = BeginAcquisition(token);
        return AcquireAsync<ValueTask, CancellationTokenOnly>(context, ref builder);
    }

    /// <summary>
    /// Attempts to acquire this synchronizer ahead of queued callers.
    /// </summary>
    /// <remarks>
    /// This method is intended for lock upgrades whose caller retains a lock needed by callers already in the queue.
    /// If immediate acquisition is not allowed, the caller is placed at the head of the queue.
    /// </remarks>
    /// <param name="context">The context to be passed to <see cref="CanAcquire(TContext)"/>.</param>
    /// <param name="token">The token that can be used to cancel the operation.</param>
    /// <returns>The task representing asynchronous execution of this method.</returns>
    /// <exception cref="OperationCanceledException">The operation has been canceled.</exception>
    /// <exception cref="ObjectDisposedException">This object has been disposed.</exception>
    protected ValueTask AcquirePriorityAsync(TContext context, CancellationToken token)
    {
        var builder = BeginAcquisition(token);
        return AcquireAsync<ValueTask, CancellationTokenOnly>(context, ref builder, prioritize: true);
    }

    private T AcquireAsync<T, TBuilder>(TContext context, ref TBuilder builder, bool prioritize = false)
        where T : struct, IEquatable<T>
        where TBuilder : struct, ITaskBuilder<T>, allows ref struct
    {
        bool acquired;
        if (builder.IsCompleted)
        {
            // nothing to do
        }
        else if (!(acquired = TryAcquireCore(context, prioritize)) && GetAcquisitionException(context) is { } factory)
        {
            factory.As<ITaskBuilderConsumer>().Complete(ref builder);
        }
        else if (Acquire<T, TBuilder, WaitNode>(ref builder, acquired, prioritize) is { } node)
        {
            node.Context = context;
        }

        return builder.Build();
    }

    private bool TryAcquireCore(TContext context, bool bypassQueue = false)
    {
        var acquired = (bypassQueue || IsEmptyQueue || CanOvertakeSuspendedCallers(context)) && CanAcquire(context);
        if (acquired)
        {
            AcquireCore(context);
        }

        return acquired;
    }
    
    private interface ITaskBuilderConsumer
    {
        void Complete<TBuilder>(ref TBuilder builder)
            where TBuilder : struct, ITaskBuilder, allows ref struct;
    }
    
    /// <summary>
    /// Represents the exception factory.
    /// </summary>
    protected abstract class ExceptionFactory : ITaskBuilderConsumer
    {
        private protected ExceptionFactory()
        {
        }

        void ITaskBuilderConsumer.Complete<TBuilder>(ref TBuilder builder)
            => Debug.Fail("Must not be called.");

        internal abstract Exception CreateException();

        /// <summary>
        /// Gets a factory for the specified exception type.
        /// </summary>
        /// <typeparam name="TException">The type of the provided exception.</typeparam>
        /// <returns>The exception factory.</returns>
        public static ExceptionFactory Of<TException>()
            where TException : Exception, new()
            => ExceptionFactory<TException>.Instance;
    }

    private sealed class ExceptionFactory<TException> : ExceptionFactory, ISingleton<ExceptionFactory<TException>>, ITaskBuilderConsumer
        where TException : Exception, new()
    {
        public static ExceptionFactory<TException> Instance { get; } = new();
        
        private ExceptionFactory()
        {
            
        }

        void ITaskBuilderConsumer.Complete<TBuilder>(ref TBuilder builder)
            => builder.Complete<DefaultExceptionFactory<TException>>();

        internal override TException CreateException() => new();
    }
}