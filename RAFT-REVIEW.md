# Raft implementation review

Reviewed on 2026-09-06 against commit
`d46d2985910e1b1f1ec44a92cce71b413d956e1d` (6.7.1).
Updated after correlating a second review of the same revision.

## Summary

The consolidated review identifies **17 actionable correctness and operability
issues**. The most serious allow committing writes that only a minority of nodes
hold and forgetting acknowledged replication on restart. The second review
identified three additional confirmed defects, recorded as findings 15-17;
the original numbering is preserved.

Authentication, transport security, and network isolation are host
responsibilities. No bypass of correctly configured host protections was
established. Unprotected RPC exposure is a deployment risk, not an additional
standalone core vulnerability.

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

## Additional findings from the second review

Paths in this section are relative to the Raft directory stated in the summary,
not implicitly to `StateMachine\`.

### 15. P1: Acknowledged replication can be forgotten on restart

**Second-review claim:** C1

**Location:** `RaftCluster.cs:717-724`;
`StateMachine\WriteAheadLog.cs:123-129,427-452`;
`StateMachine\WriteAheadLog.Flusher.cs:41-54`

The follower returns successful replication after appending entries without
ensuring that the appended tail is durable. Background flushing tracks the
locally committed boundary, not a separate durable appended boundary. More
fundamentally, reopening the WAL restores the checkpoint/snapshot boundary and
ignores a later uncommitted tail even if its pages reached disk.

Actual WAL probes reproduced a tail of 1 and commit index of 0 reopening as
tail 0 and commit index 0. This occurred with orderly reopening and abrupt
process termination, including a control with CRC64 enabled and the entry's
pages explicitly flushed to disk. These were WAL-level probes; the RPC
acknowledgment path was source-traced, not exercised as a complete distributed
crash scenario.

A valid three-node failure sequence is:

1. L replicates entry N to F1, whose local commit index is still N-1.
2. F1 acknowledges N; L commits and persists it and acknowledges the client.
3. Before F1 learns the new commit index, L becomes unavailable and F1 restarts.
4. F1 forgets N. F1 and F2 can elect a leader without the acknowledged write.

L's disk need not lose N: its absence from the newly elected majority already
violates Raft's guarantee.

This was a material omission from the first review and is distinct from the
explicit-flush off-by-one error in finding 9. The existing
`src\DotNext.Tests\Net\Cluster\Consensus\Raft\StateMachine\WriteAheadLogTests.cs:293-316`
expects five appended, three committed entries to reopen as three. Tail
dropping is intentional WAL behavior, but using that behavior to acknowledge
persistent Raft replication is unsafe. Configurable checkpoint scheduling does
not remove the requirement to preserve acknowledged replication.

**Fix:** Persist entries before positive replication acknowledgment, and recover
a durable appended boundary independently of the committed boundary. Group
commit can amortize persistence costs. CRC-based tail scanning is one possible
design, not a requirement; a correctly ordered durable-tail manifest is another.
Recovery must account for overwritten/truncated tails and must not treat all
recovered entries as committed. Adding fsync alone or documenting a weaker
guarantee does not restore Raft's acknowledged-write safety.

### 16. P1: Heartbeat worker failure leaves leadership active

**Second-review claim:** O1

**Location:** `LeaderState.cs:58-106,256-259,274-286`

An unexpected exception faults `DoHeartbeats` without transitioning out of
leader state or cancelling its leadership token. The task is subsequently
observed with exception suppression during disposal. An in-memory probe of the
compiled heartbeat method, with an injected dependency exception, reproduced
a faulted task and an uncancelled leadership token.

Unlike the second review's suggested election-timeout bound, there is no
self-timeout once this worker has stopped. Without an external state transition
or shutdown, the stale leadership state can persist indefinitely. This is a
distinct supervision defect, not merely another instance of a WAL failure.

**Fix:** Supervise the heartbeat worker, log unexpected failures, and fail closed
by invalidating leadership and scheduling an appropriate state transition. Use
the existing queued-transition pattern so disposal does not await the worker
from inside itself.

### 17. P2: Metadata framing corrupts the next pooled-connection response

**Second-review claim:** O7

**Location:**
`NetworkTransport\ConnectionOriented\ProtocolStreamExtensions.cs:115-116`;
`NetworkTransport\ConnectionOriented\Client.cs:91-96`

Dictionary decoding can finish before consuming the framed message's empty
terminator. The parse succeeds, so ordinary exception cleanup does not close
the connection. Resetting protocol state then treats the leftover bytes as
part of the next response.

A listener-free probe using the compiled production writer and parsers
reproduced this with a valid metadata dictionary `{ "k": "x" repeated 500 times }`
and a transmission block setting of 300, which allocated a 512-byte buffer.
Decoding consumed 512 of 516 bytes, leaving `00 00 00 80`. After resetting
protocol state, a legitimate vote response `{ Term = 1, Value = true }` was
parsed as `{ Term = 6442450944, Value = false }`.

No malicious peer or malformed payload is required. This is a confirmed
transport correctness defect, not only the hypothetical connection-poisoning
risk described in the second review.

**Fix:** Consume the remainder of the framed metadata message, including its
terminator, before returning; close the connection on framing failure. Do not
drain a persistent socket to EOF. Cover the exact-boundary metadata-to-vote
sequence with a regression test.

## Security assessment

| # | Severity | File | Lines | Vulnerability | Confidence |
|---|----------|------|-------|---------------|------------|
| - | - | - | - | No bypass of correctly configured host security controls established | - |

The security pass examined the existing HTTP, TCP, and custom-transport entry
points; protocol framing and parsing; membership and configuration handling;
and selected persistence paths. It was not limited to an empty branch diff.

The assessment assumes **trusted, non-Byzantine peers and restricted transport
access**. mTLS, ASP.NET Core authentication/authorization, authenticating
proxies, and network isolation are host integration responsibilities. The
absence of an embedded authentication protocol is not, by itself, a library
vulnerability. Supplied TLS options use platform certificate validation;
optional TLS or absent certificate pinning does not mean validation is disabled.

An untrusted caller reaching an unprotected Raft endpoint can invoke
state-changing RPCs. Member IDs are self-asserted protocol fields, not
credentials, and ordinary server-authenticated HTTPS does not authenticate
callers. The second review's S1/S2 therefore describe deployment requirements;
S3 is a downstream consequence of leaving application-message dispatch
unprotected, not a separate core vulnerability.

There is an important documentation/integration caveat: the published hosting
recipe registers the terminal consensus handler before authentication and
authorization middleware. Middleware registered later does not protect that
handler. The host must enforce peer access at an earlier middleware boundary,
through transport/proxy authentication, or through appropriate network
isolation. Deployment protections were not verified in this review.

Do not substitute uniform unknown-member rejection for authentication.
Authorized joining nodes need catch-up access before membership is committed;
credential authorization and voting membership are distinct.

Consequently, this assessment is **not an assurance that exposing Raft RPC
endpoints to untrusted clients is safe**. The failed-snapshot publication issue
remains a durability defect, and finding 17 remains a transport correctness
defect; neither is counted again as an independent security vulnerability.

## Correlation with the second review

The C/S/O identifiers below belong to the second review, not to the numbered
findings above. Only C1, O1, and O7 add findings to the primary list. Bounds,
documentation, and deployment recommendations are retained separately from
confirmed core defects; overlapping consequences are not counted twice.

| Claim | Disposition | Assessment |
|---|---|---|
| C1: durable replication acknowledgments | Confirmed new finding 15 | Restart forgets the acknowledged uncommitted tail even when its pages were persisted. This was a material omission from the first review. |
| C2: election log freshness | Confirmed duplicate of finding 2 | Compare terms first, then indices when terms match. |
| C3: resurrecting a removed member | Conditional; stated interleaving blocked | Finding 6 leaves `membershipLock` held after failure detection, so the subsequent same-leader membership operation cannot acquire it. Loading configuration before the barrier remains suspect for inherited uncommitted configurations or after repairing that lock leak; the alternate scenario was not reproduced. |
| C4: snapshot-aware comparison | Not an independent finding | The second review itself folds this into C2 and states that snapshot-term lookup works. |
| C5: snapshot/configuration length validation | Validation omission; proposed fix too strict | Require a nonnegative configuration length and compare it against total length only when known. Unknown `Content-Length` is valid streaming behavior. Do not duplicate finding 7. |
| C6: unbounded append count | Conditional availability risk; explanation incomplete | One stalled entry is enough to block a read; a huge count is unnecessary. Completed truncation differs from an open stalled request. Bounds alone do not resolve missing effective cancellation/deadlines; preserve streaming and use overflow-safe length checks. |
| C7: metadata dictionary allocation | Resource-budget concern; crash claim overstated | Peer counts drive allocation, but ordinary parse failures are caught and the connection cleared. Bound resource use without assuming an unexplained 1,024-entry limit is compatible. |
| C8: singleton bootstrap term/no-op | No independent defect demonstrated | The startup path differs from election, but the absence of a new term/no-op alone does not prove a safety violation in a singleton. Finding 5 covers the separate lease issue. |
| S1: unauthenticated RPCs | Host security contract | Unprotected mutation is real, but no bypass of correctly enforced host protections was established. Document the middleware-ordering and bootstrap requirements above. |
| S2: plaintext/TLS defaults | Host transport-security choice | TLS is optional and supplied options use platform certificate validation. Absence of built-in mTLS or pinning is not disabled certificate validation. |
| S3: custom-message dispatch | Downstream consequence of S1 | Host/handler authorization must protect dispatch; do not count it as an independent authentication defect. |
| S4: leader open redirect | Rejected as stated | The implementation replaces host and port with the leader destination. Probes retained that destination despite attacker-controlled input. Forwarded-scheme trust is a separate deployment concern. |
| S5: torn term/vote/checkpoint records | Unproven; platform-dependent durability assumption | Missing CRC/double buffering alone does not demonstrate tearing or double voting. The records are 37 and 12 bytes, written with `WriteThrough`; filesystem/device guarantees and an explicit crash model are needed. No universal atomicity guarantee is asserted. |
| S6: replay protection | Bounded deduplication, not an established security promise | The cache is expiring, evictable, and process-local. Qualify the internal "exactly-once" wording; no cryptographic replay-protection contract was established. |
| O1: heartbeat exceptions | Confirmed new finding 16 | The task faults without invalidating leadership. The second review's election-timeout duration bound is unsupported. |
| O2: failure-induced standby | Behavior confirmed; remedy overstated | Manual recovery is available and transition failure already logs at Critical. Automatically retrying after an unknown failure is not necessarily safe. |
| O3: dangerous defaults | Conditional configuration risks | Cold start acts on empty stored configuration; independently bootstrapped singletons do not prove split brain within one correctly configured membership. Leases are opt-in, elapsed heartbeat time is deducted, and an arbitrary drift factor is not a measured safety bound. A slow follower alone need not block majority replication. |
| O4: deterministic core tests | Coverage gap confirmed | Election, lifecycle, and failure scenarios lack direct deterministic coverage. Existing component and integration tests must not be overlooked. |
| O5: missing observability | Partial hardening | Dedicated rejection metrics would help, but malformed messages can already reach exception logging. |
| O6: configuration/header validation | Mixed hardening; silent-degradation claim overstated | An underlying cache probe rejected several invalid settings, subject to the version caveat below. Explicit expiration validation and rejection of ambiguous singleton headers remain reasonable improvements; no authorization parser-differential exploit was established. |
| O7: pooled connection state | Confirmed new finding 17 | A valid exact-boundary metadata response leaves its terminator unread and corrupts the next vote response. |

### Positive assertions that do not hold generally

- The assertion that the median commit calculation satisfies Raft is contradicted
  by finding 1: `[10, 10, 6]` can commit 10 with only two of five replicas.
- Describing the strong read barrier as ReadIndex needs qualification: the
  follower path returns a cached leader index without fresh quorum confirmation,
  as described in finding 4.
- Describing snapshot temp-file/fsync/rename as safe overlooks the incoming
  failure path in finding 7. Outgoing snapshot creation has different rollback
  handling; the two paths must not be conflated.

## Scope and limitations

The review covered consensus transitions, replication and quorum handling,
read barriers and leases, membership changes, WAL persistence and recovery,
snapshot handling, and targeted transport security paths. It is not a formal
proof of Raft correctness or an exhaustive audit of every dependency.

Validation included existing targeted tests, in-memory probes of compiled
production methods, and real WAL restart probes. These do not constitute a
complete distributed fault-injection campaign. The security assessment did
not include live exploit reproduction against a network listener or
deployment-policy verification.

The cache-configuration probe used `System.Runtime.Caching` 10.0.0.5, while the
built test output contains 10.0.0.11. Its results challenge the blanket claim of
silent degradation but are not exact-version validation of every setting.
No storage-sector query requiring elevated access or physical power-loss
experiment was performed.

Findings with conditional triggers identify those conditions above; no claim
is made that every issue occurs under every configuration.
