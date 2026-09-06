# Raft implementation review

Reviewed on 2026-09-06 against commit
`d46d2985910e1b1f1ec44a92cce71b413d956e1d` (6.7.1).

## Summary

The review identified **14 actionable correctness and operability issues**.
The most serious allows committing writes that only a minority of nodes hold.
No high-confidence implementation security vulnerability was substantiated
under the trusted-peer assumptions described below.

This is a review of the existing implementation, not a branch diff. Findings
describe the reviewed revision; recording this report does not fix them.

**P1** means high priority; **P2** means medium priority. Source line numbers
refer to the reviewed commit. Unless otherwise stated, paths are relative to
`src\cluster\DotNext.Net.Cluster\Net\Cluster\Consensus\Raft\`.

## Consensus and operability

### 1. P1: Commit index does not use the full cluster's majority requirement

**Location:** `LeaderState.cs:244-248`

The leader selects the median of the responses received rather than using the
full cluster's majority requirement. In a five-node cluster with prior commit
index 7, the leader and one follower can acknowledge index 10 while a third
member acknowledges only a current-term snapshot at index 6. The barrier can
complete with responses `[10, 10, 6]`, and the median selects 10, although only
two members contain entries 8-10.

This is a valid snapshot catch-up path, not an assumption about WAL batching:
`ReplicationUtils\ReplicationProcess.cs:214-218` sends only the snapshot when
one is present, and `RaftCluster.cs:629-632` can acknowledge it as replicated
with the leader's term.

**Fix:** Select the majority-th largest replicated index using the full
membership size, not the median of the responding subset.

### 2. P1: Election log comparison rejects valid newer logs

**Location:** `PersistentStateExtensions.cs:16-19`

The comparison requires both the candidate's index and term to be at least the
local values. A candidate at `(term 2, index 11)` is therefore rejected by a
voter at `(term 1, index 100)`, despite having the more up-to-date log under
Raft's term-first ordering. Surviving nodes can become unable to elect a leader:
the newer, shorter candidate fails the index comparison, while the older,
longer candidate fails the term comparison.

**Fix:** Compare last-log terms first; compare indices only when terms match.

### 3. P1: Even-sized clusters hang when half the nodes are unavailable

**Location:** `ReplicationUtils\ReplicationState.cs:12-25`

The failure condition requires the unavailable count to reach the majority
size. For two or four members with exactly half unavailable, neither success
nor failure is reached even after all responses arrive. The replication barrier
remains incomplete, potentially blocking heartbeats, strong reads, and shutdown.
Even-sized membership is also encountered while adding or removing one member.

**Fix:** Declare quorum unreachable when the remaining available members cannot
form a majority, rather than requiring a majority of unavailable members.

### 4. P1: Follower read barriers do not establish linearizability

**Location:** `RaftCluster.cs:963-992,1054-1060`

`SynchronizeAsync()` returns the presumed leader's cached commit index without
awaiting quorum confirmation. A follower connected to an isolated former leader
can return stale data after the other partition elects a leader and commits a
write. Requesting a strong barrier on the follower does not close this gap.

**Fix:** Require a leadership-bound quorum confirmation before returning the
read index, then wait for that index to be applied on the reading follower.

### 5. P1: A usable leader lease is exposed before it is established

**Location:** `LeaderState.cs:38-40`; `LeaderState.Lease.cs:82-88`

Enabling leases creates an uncancelled lease token before the first heartbeat
or application of the current-term write barrier. `TryGetLeaseToken()` can
expose it as usable during this interval, without the conditions required for
a linearizable read.

**Fix:** Initialize the lease as invalid. Make it usable only after quorum
confirmation and application of the current-term write barrier.

### 6. P1: Failure detection permanently retains the membership lock

**Location:** `RaftCluster.cs:1320-1346`

The unavailable-member callback acquires `membershipLock` but never releases
it. This also occurs when the callback discovers that its initiating leader
state is obsolete. Subsequent membership changes fail or wait indefinitely.

**Fix:** Release the lock in `finally` whenever acquisition succeeded.

## Persistence and recovery

Paths in this section are additionally relative to `StateMachine\`.

### 7. P1: Failed snapshot transfers are published as valid snapshots

**Location:** `SimpleStateMachine.Snapshot.cs:113-129`

`writer.Commit()` runs in `finally`, including after cancellation, a truncated
transfer, or a write failure. The incomplete file receives its final
high-index filename. Restart recovery can select it as the newest snapshot even
though the failed installation never updated the in-memory snapshot.

**Fix:** Publish only after successful serialization and flushing. On failure,
dispose the writer and roll back the temporary file.

### 8. P1: Compacted no-op WALs can fail to reopen

**Location:** `WriteAheadLog.cs:52,88,156-159`

The constructor captures `snapshotIndex` before reconstructing the no-op state
machine's snapshot from the persisted checkpoint. The captured replay boundary
remains zero. After compaction has deleted early metadata pages, reopening with
a fresh no-op state machine can start replay at index 1, access deleted
metadata, and fail initialization.

**Fix:** Recalculate the replay boundary after reconstructing the no-op
state machine's snapshot.

### 9. P1: Explicit flushing can return before the latest commit is durable

**Location:** `WriteAheadLog.Flusher.cs:103-111`

`flusherPreviousIndex` denotes the next unflushed index, but the wait condition
uses `<`. Exactly one additional committed entry therefore escapes the wait.
With private-memory storage and a long periodic flush interval, explicit
`FlushAsync()` can return before that entry and its checkpoint are persisted.

**Fix:** Wait until the flushing watermark passes the captured commit target.
Publish the updated watermark before notifying waiters.

### 10. P1: Append-to-overwrite lock upgrading can deadlock

**Location:** `WriteAheadLog.LockManagement.cs:147-148`

An overwrite operation first acquires Append, then separately queues its
upgrade. If another writer is already queued, the upgrade can sit behind that
writer, which cannot proceed until the upgrader releases Append. FIFO
acquisition leaves both waiting; caller cancellation is the escape.

**Fix:** Use an upgrade-aware acquisition path that cannot queue behind a
request blocked by the upgrader, or acquire the required exclusive mode
atomically.

### 11. P1: Snapshot installation leaves the flushing boundary behind

**Location:** `WriteAheadLog.cs:330-332`;
`WriteAheadLog.Flusher.cs:89-93`

Installing a snapshot into an empty WAL advances the committed and applied
indices without creating ordinary log metadata at the snapshot index or
advancing the flushing boundary. The next flush requests nonexistent metadata
and fails. Periodic flushing can turn this into a persistent background-worker
failure.

**Fix:** Coordinate snapshot installation, checkpointing, and the flushing
boundary so subsequent page flushing covers only actual post-snapshot entries.

### 12. P1: Accepted chunk sizes can corrupt streamed entries

**Location:** `WriteAheadLog.Options.cs:127-134`;
`WriteAheadLog.PageManagement.cs:18-29`

`ChunkSize = 12288` is accepted, but the bitwise page-address arithmetic assumes
a power of two. An entry written in two 4096-byte fragments can place its second
fragment in a different page from the one subsequently read for the entry,
returning unwritten bytes instead of the second fragment. Optional hashing
does not necessarily detect the loss because it can use the same incorrect
read range.

**Fix:** Enforce power-of-two chunk sizes or use arithmetic supporting arbitrary
sizes. Account for existing WAL compatibility when changing size normalization.

### 13. P2: Snapshot installation races with applied-index publication

**Location:** `WriteAheadLog.Applier.cs:26-45`

The applier captures its target before acquiring Read and publishes the applied
index after releasing Read. Snapshot installation can advance the applied
index between these operations, after which the applier overwrites it with an
older target. Apply waiters can remain blocked, and later replay can encounter
compacted metadata.

**Fix:** Capture the target and publish completion within the same protected
read-locked region.

### 14. P2: Background flush failures strand explicit flush callers

**Location:** `WriteAheadLog.Flusher.cs:76-79,103-111`

A background flush failure interrupts application waiters but does not wake
callers awaiting `flushCompleted`. Those callers also do not inspect the stored
background failure. Without caller cancellation, they can wait indefinitely
after the worker has permanently stopped.

**Fix:** Wake flush waiters on worker failure or exit and propagate the stored
failure before waiting and after wakeup.

## Security assessment

| # | Severity | File | Lines | Vulnerability | Confidence |
|---|----------|------|-------|---------------|------------|
| - | - | - | - | No high-confidence implementation vulnerability substantiated under the reviewed trust assumptions | - |

The security pass examined the existing HTTP, TCP, and custom-transport entry
points; protocol framing and parsing; membership and configuration handling;
and selected persistence paths. It was not limited to an empty branch diff.

The assessment assumes **trusted, non-Byzantine peers and restricted transport
access**. Member IDs are not authentication, and server-authenticated TLS alone
does not authenticate callers. Deployment authentication, middleware ordering,
and network isolation were not established.

Consequently, the security result is **not an assurance that exposing Raft RPC
endpoints to untrusted clients is safe**. The failed-snapshot publication issue
is reported as a durability defect; no independent security-boundary bypass
was established.

## Scope and limitations

The review covered consensus transitions, replication and quorum handling,
read barriers and leases, membership changes, WAL persistence and recovery,
snapshot handling, and targeted transport security paths. It is not a formal
proof of Raft correctness or an exhaustive audit of every dependency.

The security assessment was static and did not include live exploit
reproduction or deployment-policy verification. Findings with conditional
triggers identify those conditions above; no claim is made that every issue
occurs under every configuration.
