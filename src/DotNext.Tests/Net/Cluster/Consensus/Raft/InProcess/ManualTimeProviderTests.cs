namespace DotNext.Net.Cluster.Consensus.Raft.InProcess;

public sealed class ManualTimeProviderTests : RaftTest
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static async Task AsyncDisposalDrainsActiveCallback(bool disposeFirst)
    {
        var clock = new ManualTimeProvider();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var completed = false;
        var callbacks = 0;
        using var timer = clock.CreateTimer(
            _ =>
            {
                Interlocked.Increment(ref callbacks);
                entered.TrySetResult();
                release.Wait(TestToken);
                Volatile.Write(ref completed, true);
            },
            null,
            TimeSpan.FromMilliseconds(1),
            TimeSpan.FromMilliseconds(1));

        var advance = Task.Run(() => clock.Advance(TimeSpan.FromMilliseconds(1)), TestToken);
        try
        {
            await entered.Task.WaitAsync(TestToken);
            if (disposeFirst)
                timer.Dispose();

            var draining = timer.DisposeAsync();
            False(draining.IsCompleted);
            var repeatedDisposal = timer.DisposeAsync();
            False(repeatedDisposal.IsCompleted);
            release.Set();
            await draining;
            await repeatedDisposal;
            True(Volatile.Read(ref completed));
        }
        finally
        {
            release.Set();
            await advance.WaitAsync(TestToken);
        }

        False(timer.Change(TimeSpan.Zero, Timeout.InfiniteTimeSpan));
        clock.Advance(TimeSpan.FromMilliseconds(2));
        Equal(1, callbacks);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static async Task DisposedTimerDoesNotFire(bool asynchronous)
    {
        var clock = new ManualTimeProvider();
        var callbacks = 0;
        using var timer = clock.CreateTimer(
            _ => callbacks++,
            null,
            TimeSpan.FromMilliseconds(1),
            TimeSpan.FromMilliseconds(1));

        if (asynchronous)
            await timer.DisposeAsync();
        else
            timer.Dispose();

        clock.Advance(TimeSpan.FromMilliseconds(2));
        Equal(0, callbacks);
        False(timer.Change(TimeSpan.Zero, Timeout.InfiniteTimeSpan));
        await timer.DisposeAsync();
    }
}
