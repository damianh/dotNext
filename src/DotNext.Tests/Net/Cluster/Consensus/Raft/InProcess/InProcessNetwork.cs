using System.Net;

namespace DotNext.Net.Cluster.Consensus.Raft.InProcess;

using NetworkTransport;

internal enum RaftMessageType
{
    Vote,
    PreVote,
    AppendEntries,
    InstallSnapshot,
    Synchronize,
    Resign,
    Metadata,
}

internal sealed class InProcessNetwork
{
    private readonly Lock syncRoot = new();
    private readonly Dictionary<ClusterMemberId, NodeRegistration> nodes = [];
    private readonly HashSet<Link> heldLinks = [];
    private readonly HashSet<Link> blockedLinks = [];
    private readonly Dictionary<Route, Queue<InjectedFailure>> injectedFailures = [];
    private readonly List<PendingMessage> pendingMessages = [];
    private TaskCompletionSource pendingChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long messageId;

    internal IReadOnlyList<PendingMessage> PendingMessages
    {
        get
        {
            lock (syncRoot)
                return pendingMessages.Where(static message => !message.IsCompleted).ToArray();
        }
    }

    internal void Register(InProcessCluster node)
    {
        lock (syncRoot)
        {
            if (!nodes.TryAdd(node.Id, new(node)))
                throw new InvalidOperationException($"Node {node.EndPoint} is already registered.");
        }
    }

    internal async ValueTask UnregisterAsync(InProcessCluster node)
    {
        NodeRegistration registration;
        PendingMessage[] abandoned;
        lock (syncRoot)
        {
            if (!nodes.TryGetValue(node.Id, out registration) || !ReferenceEquals(registration.Node, node))
                return;

            nodes.Remove(node.Id);
            abandoned = pendingMessages
                .Where(message => message.SourceId == node.Id || message.TargetId == node.Id)
                .ToArray();
            pendingMessages.RemoveAll(message => abandoned.Contains(message));
        }

        foreach (var message in abandoned)
            message.Fail(new MemberUnavailableException(message.Member));

        await registration.DeactivateAsync().ConfigureAwait(false);
    }

    internal void Hold(EndPoint source, EndPoint target)
    {
        lock (syncRoot)
            heldLinks.Add(new(source, target));
    }

    internal void Release(EndPoint source, EndPoint target)
    {
        lock (syncRoot)
            heldLinks.Remove(new(source, target));
    }

    internal void Partition(EndPoint first, EndPoint second, bool bidirectional = true)
    {
        lock (syncRoot)
        {
            blockedLinks.Add(new(first, second));
            if (bidirectional)
                blockedLinks.Add(new(second, first));
        }
    }

    internal void Heal(EndPoint first, EndPoint second, bool bidirectional = true)
    {
        lock (syncRoot)
        {
            blockedLinks.Remove(new(first, second));
            if (bidirectional)
                blockedLinks.Remove(new(second, first));
        }
    }

    internal Task FailNext(EndPoint source, EndPoint target, RaftMessageType messageType, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        lock (syncRoot)
        {
            var route = new Route(source, target, messageType);
            if (!injectedFailures.TryGetValue(route, out var failures))
                injectedFailures.Add(route, failures = new());

            var failure = new InjectedFailure(exception);
            failures.Enqueue(failure);
            return failure.Completion.Task;
        }
    }

    internal async Task<PendingMessage> WaitForMessageAsync(
        EndPoint source, EndPoint target, RaftMessageType messageType, CancellationToken token)
    {
        var sourceId = ClusterMemberId.FromEndPoint(source);
        var targetId = ClusterMemberId.FromEndPoint(target);
        for (;;)
        {
            Task changed;
            lock (syncRoot)
            {
                var message = pendingMessages.FirstOrDefault(message =>
                    !message.IsCompleted && message.SourceId == sourceId
                    && message.TargetId == targetId && message.MessageType == messageType);
                if (message is not null)
                    return message;

                changed = pendingChanged.Task;
            }

            await changed.WaitAsync(token).ConfigureAwait(false);
        }
    }

    internal Task<TResult> SendAsync<TResult>(
        InProcessClusterMember member,
        RaftMessageType messageType,
        Func<ILocalMember, CancellationToken, ValueTask<TResult>> action,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(action);

        var source = member.Source;
        var target = member.EndPoint;
        var sourceId = source.Id;
        var targetId = member.Id;
        PendingMessage<TResult> message = null;
        DispatchRegistration dispatch = null;
        Exception failure = null;

        lock (syncRoot)
        {
            var link = new Link(source.EndPoint, target);
            var route = new Route(source.EndPoint, target, messageType);

            if (token.IsCancellationRequested)
                return Task.FromCanceled<TResult>(token);

            if (!nodes.TryGetValue(sourceId, out var sourceNode)
                || !ReferenceEquals(sourceNode.Node, source)
                || blockedLinks.Contains(link)
                || !nodes.TryGetValue(targetId, out var targetNode))
            {
                failure = new MemberUnavailableException(member);
            }
            else if (injectedFailures.TryGetValue(route, out var failures) && failures.TryDequeue(out var injected))
            {
                failure = injected.Exception;
                injected.Completion.TrySetResult();
                if (failures.Count is 0)
                    injectedFailures.Remove(route);
            }
            else if (heldLinks.Contains(link))
            {
                dispatch = new(sourceNode, targetNode);
                message = new(
                    Interlocked.Increment(ref messageId),
                    member,
                    sourceId,
                    targetId,
                    messageType,
                    dispatch,
                    action,
                    token,
                    RemovePending);
                pendingMessages.Add(message);
                pendingChanged.TrySetResult();
                pendingChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            else
            {
                dispatch = new(sourceNode, targetNode);
            }
        }

        if (message is { IsCompleted: true })
            RemovePending(message);

        return failure switch
        {
            not null => Task.FromException<TResult>(failure),
            null when message is not null => message.Task,
            _ => dispatch.InvokeAsync(member, action, token),
        };
    }

    internal async Task DeliverAsync(PendingMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        lock (syncRoot)
        {
            if (!pendingMessages.Remove(message))
                throw new InvalidOperationException("The message is not pending.");
        }

        await message.DeliverAsync().ConfigureAwait(false);
    }

    internal void Drop(PendingMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        lock (syncRoot)
        {
            if (!pendingMessages.Remove(message))
                throw new InvalidOperationException("The message is not pending.");
        }

        message.Fail(new MemberUnavailableException(message.Member));
    }

    internal void CancelPending(EndPoint source, EndPoint target)
    {
        PendingMessage[] canceled;
        var sourceId = ClusterMemberId.FromEndPoint(source);
        var targetId = ClusterMemberId.FromEndPoint(target);
        lock (syncRoot)
        {
            canceled = pendingMessages
                .Where(message => message.SourceId == sourceId && message.TargetId == targetId)
                .ToArray();
            pendingMessages.RemoveAll(message => canceled.Contains(message));
        }

        foreach (var message in canceled)
            message.Fail(new MemberUnavailableException(message.Member));
    }

    private void RemovePending(PendingMessage message)
    {
        lock (syncRoot)
            pendingMessages.Remove(message);
    }

    private readonly record struct Link(ClusterMemberId Source, ClusterMemberId Target)
    {
        internal Link(EndPoint source, EndPoint target)
            : this(ClusterMemberId.FromEndPoint(source), ClusterMemberId.FromEndPoint(target))
        {
        }
    }

    private readonly record struct Route(ClusterMemberId Source, ClusterMemberId Target, RaftMessageType MessageType)
    {
        internal Route(EndPoint source, EndPoint target, RaftMessageType messageType)
            : this(ClusterMemberId.FromEndPoint(source), ClusterMemberId.FromEndPoint(target), messageType)
        {
        }
    }

    private sealed record InjectedFailure(Exception Exception)
    {
        internal TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

internal abstract class PendingMessage
{
    private protected PendingMessage(
        long id,
        InProcessClusterMember member,
        ClusterMemberId sourceId,
        ClusterMemberId targetId,
        RaftMessageType messageType,
        DispatchRegistration dispatch)
    {
        Id = id;
        Member = member;
        SourceId = sourceId;
        TargetId = targetId;
        MessageType = messageType;
        Dispatch = dispatch;
    }

    internal long Id { get; }

    internal InProcessClusterMember Member { get; }

    internal ClusterMemberId SourceId { get; }

    internal ClusterMemberId TargetId { get; }

    internal RaftMessageType MessageType { get; }

    internal DispatchRegistration Dispatch { get; }

    internal abstract bool IsCompleted { get; }

    internal abstract Task Completion { get; }

    internal abstract Task DeliverAsync();

    internal abstract void Fail(Exception exception);
}

file sealed class PendingMessage<TResult> : PendingMessage
{
    private readonly Func<ILocalMember, CancellationToken, ValueTask<TResult>> action;
    private readonly CancellationToken token;
    private readonly Action<PendingMessage> remove;
    private readonly TaskCompletionSource<TResult> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenRegistration cancellationRegistration;
    private int state; // 0: queued, 1: dispatching, 2: completed without dispatch

    internal PendingMessage(
        long id,
        InProcessClusterMember member,
        ClusterMemberId sourceId,
        ClusterMemberId targetId,
        RaftMessageType messageType,
        DispatchRegistration dispatch,
        Func<ILocalMember, CancellationToken, ValueTask<TResult>> action,
        CancellationToken token,
        Action<PendingMessage> remove)
        : base(id, member, sourceId, targetId, messageType, dispatch)
    {
        this.action = action;
        this.token = token;
        this.remove = remove;
        cancellationRegistration = token.UnsafeRegister(
            static state => ((PendingMessage<TResult>)state).Cancel(),
            this);
        if (IsCompleted)
            cancellationRegistration.Unregister();
    }

    internal Task<TResult> Task => completion.Task;

    internal override bool IsCompleted => completion.Task.IsCompleted;

    internal override Task Completion => completion.Task;

    internal override async Task DeliverAsync()
    {
        if (Interlocked.CompareExchange(ref state, 1, 0) is not 0)
        {
            cancellationRegistration.Dispose();
            return;
        }

        try
        {
            var result = await Dispatch.InvokeAsync(Member, action, token).ConfigureAwait(false);
            completion.TrySetResult(result);
        }
        catch (OperationCanceledException e)
        {
            completion.TrySetCanceled(e.CancellationToken);
        }
        catch (Exception e)
        {
            completion.TrySetException(e);
        }
        finally
        {
            cancellationRegistration.Dispose();
        }
    }

    internal override void Fail(Exception exception)
    {
        cancellationRegistration.Dispose();
        if (Interlocked.CompareExchange(ref state, 2, 0) is 0)
            completion.TrySetException(exception);
    }

    private void Cancel()
    {
        // Once dispatch starts, its completion owns the payload until the handler exits.
        if (Interlocked.CompareExchange(ref state, 2, 0) is 0)
        {
            completion.TrySetCanceled(token);
            remove(this);
            cancellationRegistration.Unregister();
        }
    }
}

internal sealed class NodeRegistration
{
    private readonly Lock syncRoot = new();
    private readonly TaskCompletionSource drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource lifetime = new();
    private int activeDispatches;
    private bool active = true;
    private Task deactivation;

    internal NodeRegistration(InProcessCluster node)
    {
        Node = node;
        LifetimeToken = lifetime.Token;
    }

    internal InProcessCluster Node { get; }

    internal ValueTask DeactivateAsync()
    {
        lock (syncRoot)
        {
            active = false;
            return new(deactivation ??= CancelAndDrainAsync(
                activeDispatches is 0 ? Task.CompletedTask : drained.Task));
        }
    }

    private async Task CancelAndDrainAsync(Task draining)
    {
        await lifetime.CancelAsync().ConfigureAwait(false);
        await draining.ConfigureAwait(false);
        lifetime.Dispose();
    }

    internal bool TryEnter()
    {
        lock (syncRoot)
        {
            if (!active)
                return false;

            activeDispatches++;
            return true;
        }

    }

    internal CancellationToken LifetimeToken { get; }

    internal void Exit()
    {
        lock (syncRoot)
        {
            if (--activeDispatches is 0 && !active)
                drained.TrySetResult();
        }
    }
}

internal sealed class DispatchRegistration(NodeRegistration source, NodeRegistration target)
{
    internal async Task<TResult> InvokeAsync<TResult>(
        InProcessClusterMember member,
        Func<ILocalMember, CancellationToken, ValueTask<TResult>> action,
        CancellationToken token)
    {
        if (!source.TryEnter())
            throw new MemberUnavailableException(member);

        var targetEntered = false;
        try
        {
            if (!ReferenceEquals(source, target) && !(targetEntered = target.TryEnter()))
                throw new MemberUnavailableException(member);

            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
                token, source.LifetimeToken, target.LifetimeToken);
            try
            {
                cancellation.Token.ThrowIfCancellationRequested();
                return await action(target.Node, cancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested
                && (source.LifetimeToken.IsCancellationRequested || target.LifetimeToken.IsCancellationRequested))
            {
                throw new MemberUnavailableException(member);
            }
        }
        finally
        {
            if (targetEntered)
                target.Exit();

            source.Exit();
        }
    }
}
