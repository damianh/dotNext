using DotNext.Benchmarks.DurableWrite.Oracles;

namespace DotNext.Net.Cluster.Consensus.Raft.InProcess;

/// <summary>
/// Shows that the online oracles of the durable-write load tool (<c>src/DotNext.Benchmarks.DurableWrite</c>) catch
/// the failures they are meant to catch, using synthetic histories. The tool's <c>--inject</c> modes show the same
/// against a running cluster.
/// </summary>
public sealed class DurableWriteOracleTests : Test
{
    private static readonly WriteKey A = new(WriteKey.ClosedLoop, 0, 1L);
    private static readonly WriteKey B = new(WriteKey.ClosedLoop, 0, 2L);
    private static readonly WriteKey C = new(WriteKey.ClosedLoop, 1, 1L);

    private static AppliedEntry Entry(long index, WriteKey? key, long term = 1L) => new(index, term, key);

    private static void ApplyAll(OnlineHistoryChecker checker, int node, params AppliedEntry[] entries)
    {
        foreach (var entry in entries)
            checker.OnApplied(node, entry);
    }

    private static AppliedEntry[] Log(params WriteKey?[] keys)
        => keys.Select(static (k, i) => k is null ? AppliedEntry.CreateSkipped(i + 1L) : Entry(i + 1L, k)).ToArray();

    [Fact]
    public static void ConsistentHistoryPasses()
    {
        var checker = new OnlineHistoryChecker(3);
        checker.OnLeaderClaim(0, 1L);

        // Index 1 is the leader's no-op: the WAL does not pass it to the state machine, so every node records it as skipped.
        var log = Log(null, A, C, B);
        for (var node = 0; node < 3; node++)
            ApplyAll(checker, node, log);

        Equal(2L, checker.OnAcknowledged(0, A));
        Equal(4L, checker.OnAcknowledged(0, B));
        checker.CheckFinal([log, log, log]);

        Null(checker.Violation);
        Null(DurabilityAudit.Check(checker.Acknowledged, [log, log, log]));
    }

    [Fact]
    public static void DroppedEntryIsAnApplyOrderViolation()
    {
        var checker = new OnlineHistoryChecker(2);
        ApplyAll(checker, 0, Log(A, B, C));

        // Node 1 drops index 2, as the drop-applied injection does: the next entry it applies is out of order.
        ApplyAll(checker, 1, Entry(1L, A), Entry(3L, C));

        Equal(OnlineHistoryChecker.ApplyOrder, checker.Violation?.Oracle);
    }

    [Fact]
    public static void DroppedEntryRecordedAsSkippedIsAnApplyOrderViolation()
    {
        var checker = new OnlineHistoryChecker(2);
        ApplyAll(checker, 0, Log(A, B, C));
        ApplyAll(checker, 1, Entry(1L, A), AppliedEntry.CreateSkipped(2L), Entry(3L, C));

        Equal(OnlineHistoryChecker.ApplyOrder, checker.Violation?.Oracle);
        Contains("skipped index 2", checker.Violation?.Message);
    }

    [Fact]
    public static void ReorderedEntriesAreDetected()
    {
        var checker = new OnlineHistoryChecker(2);
        ApplyAll(checker, 0, Log(A, C));

        // Node 1 applies the same two writes, swapped.
        ApplyAll(checker, 1, Entry(1L, C), Entry(2L, A));

        Equal(OnlineHistoryChecker.PrefixAgreement, checker.Violation?.Oracle);
    }

    [Fact]
    public static void ClosedLoopClientOrderIsChecked()
    {
        var checker = new OnlineHistoryChecker(1);

        // B is the second write of client 0, so it cannot be committed before A.
        ApplyAll(checker, 0, Entry(1L, B), Entry(2L, A));

        Equal(OnlineHistoryChecker.ApplyOrder, checker.Violation?.Oracle);
    }

    [Fact]
    public static void WriteAppliedTwiceIsDetected()
    {
        var checker = new OnlineHistoryChecker(1);
        ApplyAll(checker, 0, Entry(1L, C), Entry(2L, C));

        Equal(OnlineHistoryChecker.ApplyOrder, checker.Violation?.Oracle);
    }

    [Fact]
    public static void TermDisagreementIsDetected()
    {
        var checker = new OnlineHistoryChecker(2);
        checker.OnApplied(0, Entry(1L, A, term: 1L));
        checker.OnApplied(1, Entry(1L, A, term: 2L));

        Equal(OnlineHistoryChecker.PrefixAgreement, checker.Violation?.Oracle);
    }

    [Fact]
    public static void AcknowledgedButNotAppliedIsDetected()
    {
        var checker = new OnlineHistoryChecker(1);
        Equal(-1L, checker.OnAcknowledged(0, A));

        Equal(OnlineHistoryChecker.AcknowledgedWrites, checker.Violation?.Oracle);
    }

    [Fact]
    public static void TwoLeadersInOneTermAreDetected()
    {
        var checker = new OnlineHistoryChecker(2);
        checker.OnLeaderClaim(0, 3L);
        checker.OnLeaderClaim(1, 3L);

        Equal(OnlineHistoryChecker.ElectionSafety, checker.Violation?.Oracle);
    }

    [Fact]
    public static void SnapshotRestoreResetsTheApplyPosition()
    {
        var checker = new OnlineHistoryChecker(2);
        var log = Log(A, C, B);
        ApplyAll(checker, 0, log);

        // Node 1 installs a snapshot of the first two entries, then applies the third.
        checker.OnRestored(1, log[..2]);
        Equal(2L, checker.LastApplied(1));
        checker.OnApplied(1, log[2]);

        Null(checker.Violation);
    }

    [Fact]
    public static void SnapshotWithAnotherHistoryIsDetected()
    {
        var checker = new OnlineHistoryChecker(2);
        ApplyAll(checker, 0, Log(A, C));
        checker.OnRestored(1, Log(C, A));

        Equal(OnlineHistoryChecker.PrefixAgreement, checker.Violation?.Oracle);
    }

    [Fact]
    public static void AcknowledgedWriteMissingFromEveryFinalPrefixIsDetected()
    {
        var checker = new OnlineHistoryChecker(2);
        ApplyAll(checker, 0, Log(A, C));
        checker.OnAcknowledged(0, C);

        // At the end, no node reports a prefix that reaches index 2.
        checker.CheckFinal([Log(A), Log(A)]);

        Equal(OnlineHistoryChecker.AcknowledgedWrites, checker.Violation?.Oracle);
    }

    [Fact]
    public static void FirstViolationWins()
    {
        var checker = new OnlineHistoryChecker(1);
        checker.OnAcknowledged(0, A);
        ApplyAll(checker, 0, Entry(2L, A));

        Equal(OnlineHistoryChecker.AcknowledgedWrites, checker.Violation?.Oracle);
    }

    [Fact]
    public static void DurabilityRequiresAMajority()
    {
        var acknowledged = new AcknowledgedWrite[] { new(A, 0, 1L), new(C, 0, 2L) };
        var full = Log(A, C);

        Null(DurabilityAudit.Check(acknowledged, [full, full, Log()]));

        // Followers that acknowledge without persisting, as the non-durable-followers injection does, recover nothing.
        var failure = DurabilityAudit.Check(acknowledged, [full, Log(), Log()]);
        Equal(OnlineHistoryChecker.Durability, failure?.Oracle);
        Contains("only 1 of 3", failure?.Message);
    }

    [Fact]
    public static void DurabilityComparesTheWriteAtTheIndex()
    {
        var acknowledged = new AcknowledgedWrite[] { new(C, 0, 2L) };

        var failure = DurabilityAudit.Check(acknowledged, [Log(A, C), Log(A, B), Log(A, B)]);

        Equal(OnlineHistoryChecker.Durability, failure?.Oracle);
    }

    [Fact]
    public static void DurabilityRejectsAGapInTheRecoveredLog()
    {
        var failure = DurabilityAudit.Check([], [[Entry(1L, A), Entry(3L, C)]]);

        Equal(OnlineHistoryChecker.Durability, failure?.Oracle);
    }

    [Fact]
    public static void DurabilityRejectsAnUnappliedAcknowledgement()
    {
        var failure = DurabilityAudit.Check([new(A, 0, -1L)], [Log(A)]);

        Equal(OnlineHistoryChecker.Durability, failure?.Oracle);
    }

    [Fact]
    public static void WriteKeyRoundTrips()
    {
        Span<byte> payload = stackalloc byte[WriteKey.HeaderSize + 3];
        var key = new WriteKey(WriteKey.OpenLoop, 42, 1234567890123L);
        key.Write(payload);

        Equal(key, WriteKey.TryRead(payload));
        Null(WriteKey.TryRead(payload[..(WriteKey.HeaderSize - 1)]));
        Equal("m1-c42-s1234567890123", key.ToString());
    }
}
