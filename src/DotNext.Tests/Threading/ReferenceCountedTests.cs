namespace DotNext.Threading;

public sealed class ReferenceCountedTests : Test
{
    [Fact]
    public static void ReleaseViaOwnership()
    {
        var obj = new MyDisposableObject();
        new ReferenceCountedOwner<MyDisposableObject>(obj).Dispose();
        True(obj.IsDisposed);
    }

    [Fact]
    public static void CaptureWithStrongRef()
    {
        var obj = new MyDisposableObject();
        ReferenceCounted<MyDisposableObject>.Scope accessor;
        var arc = new ReferenceCountedOwner<MyDisposableObject>(obj);
        accessor = arc.Acquire();
        arc.Dispose();
        
        True(accessor.IsValid);
        False(obj.IsDisposed);
        Same(obj, accessor.Value);
        Same(obj, accessor.ValueRef);
        
        accessor.Dispose();
        True(obj.IsDisposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static void CopiedScopeReleasesOnce(bool releaseOwnerFirst)
    {
        var obj = new MyDisposableObject();
        using var arc = new ReferenceCountedOwner<MyDisposableObject>(obj);
        var scope = arc.Acquire();
        var copy = scope;
        var independent = arc.Acquire();

        if (releaseOwnerFirst)
            arc.Dispose();

        copy.Dispose();
        False(scope.IsValid);
        False(copy.IsValid);
        Throws<InvalidOperationException>(() => scope.Value);
        Throws<InvalidOperationException>(() => scope.ValueRef);
        IOptionMonad<MyDisposableObject> option = scope;
        False(option.HasValue);
        Null(option.ValueOrDefault);
        scope.Dispose();
        copy.Dispose();
        arc.Dispose();

        True(independent.IsValid);
        Same(obj, independent.Value);
        False(obj.IsDisposed);
        Equal(0, obj.DisposeCount);

        independent.Dispose();
        Equal(1, obj.DisposeCount);
        False(arc.Acquire().IsValid);
    }

    [Fact]
    public static void BoxedScopeReleasesOnce()
    {
        var obj = new MyDisposableObject();
        using var arc = new ReferenceCountedOwner<MyDisposableObject>(obj);
        var scope = arc.Acquire(out var actual);
        Same(obj, actual);
        IDisposable boxed = scope;
        arc.Dispose();

        boxed.Dispose();
        False(scope.IsValid);
        scope.Dispose();
        boxed.Dispose();
        Equal(1, obj.DisposeCount);
    }

    [Fact]
    public static void ThrowingDisposalInvalidatesCopies()
    {
        var obj = new MyDisposableObject(throwOnDispose: true);
        using var arc = new ReferenceCountedOwner<MyDisposableObject>(obj);
        var scope = arc.Acquire();
        var copy = scope;
        arc.Dispose();

        Throws<InvalidOperationException>(() => scope.Dispose());
        False(scope.IsValid);
        False(copy.IsValid);
        scope.Dispose();
        copy.Dispose();
        Equal(1, obj.DisposeCount);
        False(arc.Acquire().IsValid);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task ConcurrentCopiedScopeDisposal()
    {
        var obj = new MyDisposableObject();
        using var arc = new ReferenceCountedOwner<MyDisposableObject>(obj);
        var scope = arc.Acquire();
        var independent = arc.Acquire();
        arc.Dispose();

        var tasks = new Task[100];
        for (var i = 0; i < tasks.Length; i++)
        {
            var copy = scope;
            tasks[i] = Task.Run(() => copy.Dispose(), TestToken);
        }

        await Task.WhenAll(tasks).WaitAsync(TestToken);
        False(scope.IsValid);
        True(independent.IsValid);
        Same(obj, independent.Value);
        Equal(0, obj.DisposeCount);

        independent.Dispose();
        Equal(1, obj.DisposeCount);
    }

    [Theory(Timeout = TestTimeouts.Default)]
    [InlineData(10)]
    [InlineData(100)]
    public static async Task ConcurrentAccess(int concurrentFlows)
    {
        using var counter = new AsyncCountdownEvent(concurrentFlows);
        var obj = new MyDisposableObject();
        ReferenceCounted<MyDisposableObject>.Scope accessor;
        using (var arc = new ReferenceCountedOwner<MyDisposableObject>(obj))
        {
            ReferenceCounted<MyDisposableObject> weak = arc;
            accessor = weak.Acquire();
            True(accessor.IsValid);
            
            for (var i = 0; i < concurrentFlows; i++)
            {
                ThreadPool.UnsafeQueueUserWorkItem(static args =>
                    {
                        using var strong = args.weak.Acquire();
                        True(strong.IsValid);
                        args.counter.Signal();
                    },
                    (weak, counter),
                    preferLocal: false);
            }
        }
        
        await counter.WaitAsync(TestToken);
        accessor.Dispose();
        
        await obj.Task.WaitAsync(TestToken);
    }

    [Fact]
    public static void InvalidScope()
    {
        Throws<InvalidOperationException>(static () => default(ReferenceCounted<MyDisposableObject>.Scope).Value);
        Throws<InvalidOperationException>(static () => default(ReferenceCounted<MyDisposableObject>.Scope).ValueRef);
        False(default(ReferenceCounted<MyDisposableObject>.Scope).IsValid);
    }

    [Fact]
    public static void AcquireWithExtension()
    {
        var expected = new MyDisposableObject();
        var arc = new ReferenceCountedOwner<MyDisposableObject>(expected);
        MyDisposableObject actual;
        using (arc.Acquire(out actual))
        {
            Same(expected, actual);
        }
        
        arc.Dispose();
        False(arc.Acquire(out actual).IsValid);
        Null(actual);
    }
}

file sealed class MyDisposableObject(bool throwOnDispose = false) : TaskCompletionSource, IDisposable
{
    private int disposeCount;

    public int DisposeCount => Volatile.Read(ref disposeCount);

    public bool IsDisposed => Task.IsCompletedSuccessfully;

    public void Dispose()
    {
        Interlocked.Increment(ref disposeCount);
        if (throwOnDispose)
            throw new InvalidOperationException();

        TrySetResult();
    }
}