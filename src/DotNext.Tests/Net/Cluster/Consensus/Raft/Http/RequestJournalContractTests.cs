using System.Reflection;

namespace DotNext.Net.Cluster.Consensus.Raft.Http;

using Messaging;
using static BindingFlags;

public sealed class RequestJournalContractTests : Test
{
    private static readonly Assembly TransportAssembly = typeof(RequestJournalConfiguration).Assembly;
    private static readonly Type DetectorType = TransportAssembly.GetType(
        "DotNext.Net.Cluster.Consensus.Raft.Http.DuplicateRequestDetector", throwOnError: true)!;
    private static readonly Type CustomMessageType = TransportAssembly.GetType(
        "DotNext.Net.Cluster.Consensus.Raft.Http.CustomMessage", throwOnError: true)!;
    private static readonly ConstructorInfo DetectorConstructor = DetectorType.GetConstructor(
        Instance | NonPublic,
        [typeof(RequestJournalConfiguration)])!;
    private static readonly ConstructorInfo MessageConstructor = CustomMessageType.GetConstructor(
        Instance | NonPublic,
        [typeof(ClusterMemberId).MakeByRefType(), typeof(IMessage), typeof(bool)])!;
    private static readonly MethodInfo IsDuplicatedMethod = DetectorType.GetMethod(
        "IsDuplicated",
        Instance | NonPublic)!;
    private static readonly MethodInfo TrimMethod = DetectorType.GetMethod(
        "Trim",
        Instance | Public)!;
    private static readonly MethodInfo GetCountMethod = DetectorType.GetMethod(
        "GetCount",
        Instance | Public,
        binder: null,
        [typeof(string)],
        modifiers: null)!;
    private static readonly PropertyInfo CacheMemoryLimitProperty = DetectorType.GetProperty(
        "CacheMemoryLimit",
        Instance | Public)!;

    [Fact(Timeout = TestTimeouts.Default)]
    public static void ImmediateRedeliveryIsSuppressed()
    {
        using var detector = CreateDetector();
        var message = CreateMessage();

        False(IsDuplicated(detector, message));
        True(IsDuplicated(detector, message));
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public static void RedeliveryAfterCacheEvictionIsAccepted()
    {
        using var detector = CreateDetector(expiration: TimeSpan.FromMinutes(1), memoryLimit: 1L);
        var message = CreateMessage();

        False(IsDuplicated(detector, message));
        Equal(1L << 20, (long)CacheMemoryLimitProperty.GetValue(detector.Target)!);

        Equal(1L, (long)TrimMethod.Invoke(detector.Target, [100])!);
        Equal(0L, GetCount(detector));

        False(IsDuplicated(detector, message));
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public static async Task RedeliveryAfterExpirationIsAccepted()
    {
        using var detector = CreateDetector(expiration: TimeSpan.FromMilliseconds(10));
        var message = CreateMessage();

        False(IsDuplicated(detector, message));
        await Task.Delay(TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken);
        False(IsDuplicated(detector, message));
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public static void RedeliveryAfterRestartIsAccepted()
    {
        var message = CreateMessage();
        using (var detector = CreateDetector())
            False(IsDuplicated(detector, message));

        using var restartedDetector = CreateDetector();
        False(IsDuplicated(restartedDetector, message));
    }

    [Theory(Timeout = TestTimeouts.Default)]
    [InlineData(-1L)]
    [InlineData(2_147_483_648L)]
    public static void InvalidMemoryLimitIsRejected(long value)
    {
        var exception = Throws<TargetInvocationException>(() => CreateDetector(memoryLimit: value));

        IsType<ArgumentException>(exception.InnerException);
    }

    [Theory(Timeout = TestTimeouts.Default)]
    [InlineData(0L)]
    [InlineData(-1L)]
    public static void NonPositivePollingIntervalIsRejected(long ticks)
    {
        var exception = Throws<TargetInvocationException>(
            () => CreateDetector(pollingInterval: TimeSpan.FromTicks(ticks)));

        IsType<ArgumentException>(exception.InnerException);
    }

    [Theory(Timeout = TestTimeouts.Default)]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(long.MinValue)]
    public static void NonPositiveExpirationIsRejected(long ticks)
    {
        var exception = Throws<TargetInvocationException>(
            () => CreateDetector(expiration: TimeSpan.FromTicks(ticks)));

        var inner = IsType<ArgumentOutOfRangeException>(exception.InnerException);
        Equal(nameof(RequestJournalConfiguration.Expiration), inner.ParamName);
    }

    [Theory(Timeout = TestTimeouts.Default)]
    [InlineData(long.MaxValue)] // TimeSpan.MaxValue
    [InlineData(TimeSpan.TicksPerDay * 3_000_000L)] // beyond DateTimeOffset.MaxValue from any current date
    [InlineData(TimeSpan.TicksPerDay * 365_000L)] // large, but representable
    public static void LargeExpirationDoesNotFailDelivery(long ticks)
    {
        using var detector = CreateDetector(expiration: TimeSpan.FromTicks(ticks));
        var message = CreateMessage();

        False(IsDuplicated(detector, message));
        True(IsDuplicated(detector, message));
    }

    private static Detector CreateDetector(
        TimeSpan? expiration = null,
        long? memoryLimit = null,
        TimeSpan? pollingInterval = null)
    {
        var configuration = new RequestJournalConfiguration();
        if (expiration.HasValue)
            configuration.Expiration = expiration.GetValueOrDefault();
        if (memoryLimit.HasValue)
            configuration.MemoryLimit = memoryLimit.GetValueOrDefault();
        if (pollingInterval.HasValue)
            configuration.PollingInterval = pollingInterval.GetValueOrDefault();

        return new Detector(DetectorConstructor.Invoke([configuration]));
    }

    private static object CreateMessage()
        => MessageConstructor.Invoke([new ClusterMemberId(Random.Shared), new TextMessage("payload", "name"), true]);

    private static bool IsDuplicated(Detector detector, object message)
        => (bool)IsDuplicatedMethod.Invoke(detector.Target, [message])!;

    private static long GetCount(Detector detector)
        => (long)GetCountMethod.Invoke(detector.Target, [null])!;

    private readonly struct Detector(object target) : IDisposable
    {
        internal object Target { get; } = target;

        public void Dispose() => ((IDisposable)Target).Dispose();
    }
}
