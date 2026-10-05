using System.Diagnostics;
using System.Numerics;

namespace DotNext.Benchmarks.DurableWrite;

/// <summary>
/// A thread-safe log-linear histogram of durations in microseconds, in the style of HdrHistogram:
/// each power-of-two range is split into 256 linear buckets, so the relative error is below 0.4%.
/// </summary>
internal sealed class LatencyHistogram
{
    private const int SubBucketBits = 8;
    private const int SubBuckets = 1 << SubBucketBits;
    private const int Ranges = 64 - SubBucketBits;

    private readonly long[] counts = new long[(Ranges + 1) * SubBuckets];
    private long count, sum, max;

    internal long Count => Interlocked.Read(ref count);

    internal void RecordTicks(long ticks)
        => Record(ticks <= 0L ? 0L : (long)(ticks * 1_000_000D / Stopwatch.Frequency));

    internal void RecordMilliseconds(double milliseconds)
        => Record(milliseconds <= 0D ? 0L : (long)(milliseconds * 1000D));

    internal void Record(long microseconds)
    {
        if (microseconds < 0L)
            microseconds = 0L;

        Interlocked.Increment(ref counts[BucketOf(microseconds)]);
        Interlocked.Increment(ref count);
        Interlocked.Add(ref sum, microseconds);

        for (var current = Volatile.Read(in max); microseconds > current;)
        {
            var actual = Interlocked.CompareExchange(ref max, microseconds, current);
            if (actual == current)
                break;

            current = actual;
        }
    }

    private static int BucketOf(long value)
    {
        if (value < SubBuckets)
            return (int)value;

        // value >> shift is in [256, 512), so each power-of-two range gets 256 linear buckets.
        var shift = 64 - BitOperations.LeadingZeroCount((ulong)value) - SubBucketBits - 1;
        var sub = (int)(value >> shift) - SubBuckets;
        return (shift + 1) * SubBuckets + sub;
    }

    // The upper bound of the bucket, so a percentile never under-reports.
    private static long UpperBoundOf(int bucket)
    {
        if (bucket < SubBuckets)
            return bucket;

        var shift = bucket / SubBuckets - 1;
        var sub = bucket % SubBuckets + SubBuckets;
        return ((long)(sub + 1) << shift) - 1L;
    }

    internal long Percentile(double percentile)
    {
        var total = Count;
        if (total is 0L)
            return 0L;

        var rank = (long)Math.Ceiling(percentile / 100D * total);
        if (rank < 1L)
            rank = 1L;

        var seen = 0L;
        for (var bucket = 0; bucket < counts.Length; bucket++)
        {
            seen += Volatile.Read(in counts[bucket]);
            if (seen >= rank)
                return long.Min(UpperBoundOf(bucket), Volatile.Read(in max));
        }

        return Volatile.Read(in max);
    }

    internal LatencySummary Summarize() => new()
    {
        Count = Count,
        MeanUs = Count is 0L ? 0D : Math.Round((double)Interlocked.Read(ref sum) / Count, 1),
        P50Us = Percentile(50D),
        P90Us = Percentile(90D),
        P99Us = Percentile(99D),
        P999Us = Percentile(99.9D),
        MaxUs = Volatile.Read(in max),
    };
}

internal sealed class LatencySummary
{
    public long Count { get; init; }
    public double MeanUs { get; init; }
    public long P50Us { get; init; }
    public long P90Us { get; init; }
    public long P99Us { get; init; }
    public long P999Us { get; init; }
    public long MaxUs { get; init; }

    public override string ToString() => $"p50 {Format(P50Us)} p99 {Format(P99Us)} p99.9 {Format(P999Us)} max {Format(MaxUs)}";

    private static string Format(long us) => us >= 10_000L ? $"{us / 1000D:F1}ms" : $"{us}us";
}
