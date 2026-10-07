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

**Status:** Fixed for #19. The leader captures
`max(LastCommittedEntryIndex, WriteBarrier)` as the read index, then awaits
`ForceReplicationAsync()`. That call completes only after a majority
acknowledges the leader's term in a replication round started after the
request arrived. If leadership is lost or the quorum is unreachable, the leader
returns `null` instead of an index, and the follower then waits to apply the
confirmed index. The write-barrier floor covers entries committed in earlier
terms that a new leader has not yet committed locally. The sender's commit index
no longer lets the leader skip confirmation. `FollowerReadBarrierTests` covers:
- an isolated former leader, with and without the new term observed;
- inherited commits;
- healthy, no-quorum and cancelled reads;
- snapshot lag.

### 5. P1: A usable leader lease is exposed before it is established

**Location:** `LeaderState.cs:38-40`; `LeaderState.Lease.cs:82-88`

Enabling leases creates an uncancelled lease token before the first heartbeat
or application of the current-term write barrier. `TryGetLeaseToken()` can
expose it as usable during this interval, without the conditions required for
a linearizable read.

**Fix:** Initialize the lease as invalid. Make it usable only after quorum
confirmation and application of the current-term write barrier.

**Status:** Fixed for #21. A new leader starts with an inactive (canceled)
lease. The first quorum round that confirms its term replaces it with a real
lease, and `TryGetLeaseToken` keeps returning a canceled token until the local
state machine has applied the current-term write barrier. If applying the
barrier fails, the lease stays inactive for that term. A failed or stalled
round, a replication worker failure, step-down, shutdown, and disabled leases
never publish a usable lease. `LeaderLeaseActivationTests` covers these paths
with the in-process harness and a gated state machine:

```powershell
dotnet run --project src\DotNext.Tests\DotNext.Tests.csproj --no-build -- --filter-class 'DotNext.Net.Cluster.Consensus.Raft.InProcess.LeaderLeaseActivationTests' --progress off --timeout 180s
```

On baseline `96b12d84c`, 6 of 7 tests fail.
`LeaseRequiresQuorumAndAppliedWriteBarrier` reports: "The leader exposes a
usable lease before any quorum round; after quorum confirmation, before the
write barrier is committed; after the write barrier is committed, before it is
applied." Lease timing remains covered by the
[Leader lease timing model](#leader-lease-timing-model) (#20, #58).

### 6. P1: Failure detection permanently retains the membership lock

**Location:** `RaftCluster.cs:1320-1346`

The unavailable-member callback acquires `membershipLock` but never releases
it. This also occurs when the callback discovers that its initiating leader
state is obsolete. Subsequent membership changes fail or wait indefinitely.

**Fix:** Release the lock in `finally` whenever acquisition succeeded.

**Status:** Fixed for #17. The callback releases the lock if and only if it
acquired it, including when the initiating leader state is obsolete, and
tolerates `ObjectDisposedException` only while the cluster is disposing.
`UnavailableMemberDetectionTests` covers these paths without elections or
timers. Repairing the leak exposed C3 below, fixed separately for #18.

**Follow-up (#18):** With the lock released, a membership change built from the
last *applied* configuration could overwrite a pending one and resurrect a
removed member. Add, remove, and automatic removal now wait for the leader to
apply its whole log before loading the configuration, and append the change
only if the leader's term is still the log's current term. See
[Membership change semantics](#membership-change-semantics).

**Follow-up (#51):** Losing leadership while the callback is running is an
expected outcome, not a failure to process the member. `NotLeaderException`
and cancellation of the leadership token after lock acquisition are logged at
Debug (event 74047); genuine callback failures retain
`FailedToProcessUnresponsiveMember` at Warning.

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

#23 documents this host contract in the
[Host security](src/cluster/README.md#host-security) and
[Bootstrap and membership](src/cluster/README.md#bootstrap-and-membership)
sections of the cluster README. `ConsensusHandlerHostSecurityTests` shows that an
unauthenticated request reaches the handler under the published ordering and is
rejected when authentication and authorization come first.
`HttpBootstrapRecipeTests` shows that two empty `coldStart: true` nodes form
separate clusters, that a single cold-start owner plus a joiner form one
cluster, and that a restart with persisted state does not bootstrap again. No
runtime behavior or defaults were changed.

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
| C3: resurrecting a removed member | Confirmed after finding 6 was fixed; fixed for #18 | Finding 6 originally blocked the stated interleaving. Once the lock leak was repaired, `MembershipConfigurationTests` reproduced the resurrection: a change built from the last applied configuration overwrote an unapplied automatic removal, a removal inherited by a new leader, or an earlier detection. Changes are now built only after the latest configuration in the leader's log is committed and applied (since #49; the whole log before that), and are appended only in the leader's current term. See [Membership change semantics](#membership-change-semantics). |
| C4: snapshot-aware comparison | Not an independent finding | The second review itself folds this into C2 and states that snapshot-term lookup works. |
| C5: snapshot/configuration length validation | Validation omission; proposed fix too strict | Require a nonnegative configuration length and compare it against total length only when known. Unknown `Content-Length` is valid streaming behavior. Do not duplicate finding 7. |
| C6: unbounded append count | Conditional availability risk; explanation incomplete | One stalled entry is enough to block a read; a huge count is unnecessary. Completed truncation differs from an open stalled request. Bounds alone do not resolve missing effective cancellation/deadlines; preserve streaming and use overflow-safe length checks. |
| C7: metadata dictionary allocation | Resource-budget concern; crash claim overstated | Peer counts drive allocation, but ordinary parse failures are caught and the connection cleared. Bound resource use without assuming an unexplained 1,024-entry limit is compatible. |
| C8: singleton bootstrap term/no-op | No independent defect demonstrated | The startup path differs from election, but the absence of a new term/no-op alone does not prove a safety violation in a singleton. Finding 5 covers the separate lease issue. |
| S1: unauthenticated RPCs | Host security contract | Unprotected mutation is real, but no bypass of correctly enforced host protections was established. Document the middleware-ordering and bootstrap requirements above. |
| S2: plaintext/TLS defaults | Host transport-security choice | TLS is optional and supplied options use platform certificate validation. Absence of built-in mTLS or pinning is not disabled certificate validation. |
| S3: custom-message dispatch | Downstream consequence of S1 | Host/handler authorization must protect dispatch; do not count it as an independent authentication defect. |
| S4: leader open redirect | Rejected as stated | The implementation replaces host and port with the leader destination. Probes retained that destination despite attacker-controlled input. Forwarded-scheme trust is a separate deployment concern. |
| S5: torn term/vote/checkpoint records | Unproven; platform-dependent durability assumption. #24: the crash model is documented, and a related process-crash defect (term published before it was durable) is fixed, see "Term/vote durability (#24)" | Missing CRC/double buffering alone does not demonstrate tearing or double voting. The term/vote record is 37 bytes, unchecksummed, in place, written with `WriteThrough`. (The checkpoint is no longer a small record: it is three checksummed 64 KiB generational slots plus sidecars, see FORK-DIVERGENCE.md.) Filesystem/device guarantees and an explicit crash model are needed. No universal atomicity guarantee is asserted. |
| S6: replay protection | Bounded deduplication, not an established security promise | The cache is expiring, evictable, and process-local. Qualify the internal "exactly-once" wording; no cryptographic replay-protection contract was established. |
| O1: heartbeat exceptions | Confirmed new finding 16 | The task faults without invalidating leadership. The second review's election-timeout duration bound is unsupported. |
| O2: failure-induced standby | Behavior confirmed; remedy overstated | Manual recovery is available and transition failure already logs at Critical. Automatically retrying after an unknown failure is not necessarily safe. |
| O3: dangerous defaults | Conditional configuration risks; lease timing model established for #20 | Cold start acts on empty stored configuration; independently bootstrapped singletons do not prove split brain within one correctly configured membership. Leases are opt-in, elapsed heartbeat time is deducted, and an arbitrary drift factor is not a measured safety bound. A slow follower alone need not block majority replication. See [Leader lease timing model](#leader-lease-timing-model) for the supported assumptions, the two fixed lease defects, and the remaining snapshot hazard (#58). |
| O4: deterministic core tests | Coverage gap confirmed | Election, lifecycle, and failure scenarios lack direct deterministic coverage. Existing component and integration tests must not be overlooked. |
| O5: missing observability | Partial hardening | Dedicated rejection metrics would help, but malformed messages can already reach exception logging. See [Failure signals and operator actions](#failure-signals-and-operator-actions-26) (#26): the unsupervised candidate voting task and the misattributed leader read failure (#115) are fixed. |
| O6: configuration/header validation | Mixed hardening; silent-degradation claim overstated | An underlying cache probe rejected several invalid settings, subject to the version caveat below. Explicit expiration validation and rejection of ambiguous singleton headers remain reasonable improvements; no authorization parser-differential exploit was established. Since #109 a nonpositive request journal `Expiration` is rejected at startup, and one past `DateTimeOffset.MaxValue` no longer fails delivery but leaves entries without an absolute expiration. |
| O7: pooled connection state | Confirmed new finding 17 | A valid exact-boundary metadata response leaves its terminator unread and corrupts the next vote response. |

### Positive assertions that do not hold generally

- The assertion that the median commit calculation satisfies Raft is contradicted
  by finding 1: `[10, 10, 6]` can commit 10 with only two of five replicas.
- Describing the strong read barrier as ReadIndex needed qualification: the
  follower path returned a cached leader index without fresh quorum
  confirmation, as described in finding 4. Since #19 the leader confirms its
  quorum before it returns the read index.
- Describing snapshot temp-file/fsync/rename as safe overlooks the incoming
  failure path in finding 7. Outgoing snapshot creation has different rollback
  handling; the two paths must not be conflated.

## Membership change semantics

Recorded for #18, revised for #49 and #52. Rules are the single-server change
rules from Ongaro's thesis, chapter 4.

| Rule | Status |
|---|---|
| R1: one change at a time | Holds. On one leader, `membershipLock` serializes changes. Since #49, a leader builds a change only after the latest configuration in its log and an entry of its own term are committed and applied locally (`LoadLatestConfigurationAsync` waits for `max(configuration index, write barrier)`). A change inherited from a previous leader, or appended by the failure detector, is therefore committed before the next change is appended. The whole-log apply barrier from #18 is kept only for a cluster that does not derive its configuration from the log (see below). Since #48, the rule is also enforced at the public append boundary; see [Configuration append paths](#configuration-append-paths). |
| R2: a server uses the latest configuration in its log | Holds since #49 for the TCP/UDP and HTTP hosts. The latest configuration entry in the log becomes active as soon as it is appended, committed or not, on leaders and followers. Votes, pre-votes, commit and lease quorums, and replication targets are all counted against it. See [Log-derived configuration](#log-derived-configuration). Before #49 a configuration took effect only when it was applied and `ConfigurationPollingLoop` swapped `members`, which allowed two leaders in one term (#49, CE-2). |
| R3: a new leader commits an entry of its term before changing configuration | Holds. `CandidateState` appends the election no-op; membership changes wait for it to be committed and applied (the leader's write barrier). |
| R4: catch up new servers first | Holds (`ReplicationProcess.CatchUpAsync`). Since #54, warm-up accepts any matching-prefix acknowledgment (`Replicated` or `ReplicatedWithLeaderTerm`, from AppendEntries or InstallSnapshot) that reaches the leader's commit index captured at the start of warm-up. Before, it required an entry of the leader's term, so a node that was already caught up, for example a removed node being re-added, got only empty heartbeats and was never accepted. Since #52, an actual acknowledgment is always required: a round that is rejected or reports an unsupported version never catches the member up, even when the watermark is 0. Before, `ReplicatedIndex` was 0 for such a round, so it satisfied a watermark of 0. Higher-term responses still do not count. Commit and lease quorum counting are unchanged: they still treat such acknowledgments as `Touched`. |
| R5: snapshots carry the configuration | Holds. Installing a snapshot replaces the configurations of the log prefix it covers with the configuration shipped with it. |
| R6: contain disruptive removed servers | Holds (PreVote). Pre-votes and votes are counted only from members of the active configuration. A removed server that never received its removal keeps its old configuration and may keep asking for votes; members reject it. |
| R7: a removed leader steps down | Holds. Since #49, a leader that removes itself keeps managing the cluster without counting itself until the removal is committed, then steps down (it becomes standby). `RemoveMemberAsync` returns after the step-down. |

### Log-derived configuration

`RaftCluster<TMember>.UseLogConfiguration` enables the log-derived active
configuration; the TCP/UDP (`RaftCluster`) and HTTP hosts call it before
starting. It tracks the configuration entries in the log above the last
applied configuration in `IClusterConfigurationStorage`, and publishes the
latest one as `members`:

- **Append.** A follower rescans the appended range when the entries carry a
  configuration, or when an earlier append failed part-way. A leader activates
  its own configuration entry right after appending it. The rescan runs even if
  the request is canceled after the entries are written.
- **Truncation.** When an append overwrites the log, the configurations above
  the overwritten index are dropped and the previous configuration in the log,
  or the stored (applied) configuration, becomes active again.
- **Snapshot.** Installing a snapshot drops the configurations it covers and
  takes the configuration shipped with it, which the storage now holds.
- **Restart.** On start, the active configuration is rebuilt from the stored
  configuration and the configuration entries after it in the log.
- **Candidate.** Before counting votes, a candidate refreshes a configuration
  that is stale, for example after a failed append.

The storage still receives the configuration when the entry is applied, and
still raises `ConfigurationChanged`; it is the durable, committed baseline,
not the source of `members`. The polling loops that adopted a configuration on
apply are removed from both hosts. `MemberAdded` and `MemberRemoved` fire when
a configuration becomes active, so they can fire again in reverse if an
uncommitted configuration is truncated.

A replication round that started before the leader appended a new
configuration counts its quorum over the previous configuration. That is safe
because the majorities of two configurations that differ by one server always
overlap. For the same reason, a member removed while a round is in flight is
disconnected only after the next round, which excludes it, completes.
Disconnecting it earlier can cost that round its quorum and make the leader
step down.

`AddMemberAsync` and `RemoveMemberAsync` (and so the hosts' add and remove
APIs) return after the new configuration is committed and applied by the
leader. The member appears in, or disappears from, `Members` as soon as the
configuration is appended. `UnavailableMemberDetected` still returns after
appending the removal, without waiting for the commit.
**Term guard.** The WAL and `ConsensusOnlyState` implement the internal
`ITermGuardedAuditTrail`: under the append lock, an entry whose term is not
the log's current term is rejected with `NotLeaderException`, without
modifying or poisoning the log. The check is atomic with other appends and
overwrites, not with `UpdateTermAsync` or `IncrementTermAsync`. A configuration
entry can still land after the local term advances, but never after an entry
of a newer term. That is equivalent to append-then-step-down: the old leader
state is stopped before this node votes or accepts newer-term entries, so a
late entry that was not already replicated cannot be committed and is
truncated by the next leader. Other `IPersistentState` implementations get a
best-effort pre-check. The guard covers membership appends and, since #50,
leader proposals (`ReplicateAsync`); the internal storage-level
`ClusterConfigurationExtensions.AppendAsync` overload and the raw audit trail
appends still accept a caller-supplied term (see [Leader proposal term safety](#leader-proposal-term-safety-50)).

**Resolution of the OPEN QUESTION (#49): apply-time adoption does not preserve
quorum overlap, and is replaced.**
Apply-time adoption, recorded here for #18 as a known deviation, did not
preserve quorum overlap. #49 reproduced two leaders in one term: a lagging
candidate that already held a committed configuration, but had not applied it
yet, counted votes against the previous configuration, whose majority did not
overlap with the majority that elected the other leader
(`LaggingCandidateElectionTests`). The leader-side variant (CE-1), where a
leader commits under a configuration it has already replaced, has the same
cause. Both are fixed by R2: the configuration is active on append, so every
server counts against the latest configuration in its log, and a leader
appends a new configuration only after the previous one is committed. This
follows the single-server-change argument of the thesis, and needs no
apply-time barrier. `LogDerivedConfigurationTests` covers truncation, restart,
self-removal, and CE-1.

**Limits of this evidence.** The decision is backed by the regression tests
above and by the thesis argument, not by a bounded model. No TLA+/PlusCal model
or checker run is part of this change (#49 asked for one; it was scoped out
here, together with the simulation work in #56). Individual regressions do not
prove dynamic-membership safety in general. A model that compares append-time
adoption with the fork's earlier stages remains an open follow-up.
**Custom hosts.** A `RaftCluster<TMember>` subclass that does not call
`UseLogConfiguration` keeps the previous behaviour: a configuration takes effect
when the host applies it through `ChangeConfigurationAsync`, and changes wait
for the whole log to be applied. That mode is still exposed to #49 and is kept
only for compatibility.

### Configuration append paths

Recorded for #48. The single-change rule (R1) holds for a configuration entry
that enters a leader's log through a path that takes `membershipLock`, waits
for the commit barrier, uses the term guard and activates the entry on the
leader. Before #48, `ReplicateAsync` and the public
`ClusterConfigurationExtensions.AppendAsync` put a configuration entry into a
running leader's log with none of these. The leader kept counting over the
previous member set while followers activated the entry on append (a CE-1
style divergence). The next `AddMemberAsync` or `RemoveMemberAsync` was then
built from the stale active configuration and silently reverted the bypassed
change (`ConfigurationAppendBoundaryTests`).

| Path | Status |
|---|---|
| `AddMemberAsync`, `RemoveMemberAsync` and the hosts' add and remove APIs | Supported. |
| `UnavailableMemberDetected` and the protected `UnavailableMemberDetected<TAddress>` helper | Supported. The helper must be called from the failure-detection callback, which holds `membershipLock`. |
| `RaftCluster.ReplicateAsync` (also `IReplicationCluster<IRaftLogEntry>.ReplicateAsync`, which the HTTP host registers in DI) | Rejects an entry with `IsConfiguration == true` (`ArgumentException`), in every mode. The `RaftClusterExtensions.ReplicateAsync` helpers never produce configuration entries. |
| Public `ClusterConfigurationExtensions.AppendAsync(IPersistentState, ...)` | Throws `InvalidOperationException` on the log of a started cluster that derives its configuration from the log (WAL and `ConsensusOnlyState`). On a standalone log, or one whose cluster completed `StopAsync` or was disposed, it is a plain storage-level append, as before. If `StopAsync` is canceled before the shutdown transition finishes, the log stays guarded until the cluster is disposed. |
| Raw `IPersistentState`/`IAuditTrail` appends on `AuditTrail` (`AppendAsync` of a custom entry, the producer and start-index overloads, `AppendAndCommitAsync`) | Not guarded. Followers use them for replication and snapshot installation, so they cannot reject configuration entries. Calling them on a running cluster's log is outside the membership contract. |
| Custom `IPersistentState` implementations | Not guarded; the public extension cannot tell that the log is attached to a cluster. |
| A cluster that does not call `UseLogConfiguration` | Only `ReplicateAsync` is guarded; the public extension is not blocked in that compatibility mode. |
| Candidate no-op; protected `ChangeConfigurationAsync` | Not configuration appends. `ChangeConfigurationAsync` edits `members` directly and serves only the compatibility mode. |

The removed-node side is unchanged: a node outside its own configuration still
accepts AppendEntries, and returns to the configuration when a new leader
overwrites its uncommitted removal.

The guard on the public extension is a misuse tripwire, not a lock. It reads the
managed marker before the append, so it is not atomic with the append itself: a
call that was admitted just before `StartAsync` set the marker can still land
behind the startup scan. Appending to a log directly while its cluster is
starting is outside the membership contract, like the raw appends above. Making
the check atomic would need a storage primitive that tests the marker under the
write-ahead log's append lock; that is a larger storage change and is not part
of #48.

## Leader lease timing model

Recorded for #20. A leader that completes a heartbeat round acknowledged by a
majority holds a lease for `ElectionTimeout.LowerValue / ClockDriftBound`,
measured on its monotonic clock from the start of the round. A follower
refuses PreVote and Vote while it has heard from the leader within its own
election timeout, which is at least `LowerValue`. The lease is safe when every
majority that could elect a new leader includes a member whose refusal window
outlasts the lease.

**Supported assumptions**

| Assumption | Enforcement |
|---|---|
| Monotonic clocks (`TimeProvider.GetTimestamp`) drift apart by at most `ClockDriftBound`. Wall-clock adjustments do not matter. | Operator. `LeaseExpiresBeforeVotersForgetLeaderWithinDriftBound` shows no overlap within the bound. `DriftBeyondBoundLetsVotersForgetLeaderDuringLease` shows the overlap when the bound is exceeded. |
| Timer callbacks may run arbitrarily late, for example under thread-pool starvation or GC pauses. | Fixed. The lease stores its deadline, and `TryGetLeaseToken` compares the clock against the deadline and cancels an expired lease. The timer only gives prompt notification. |
| A voter may crash and restart with its persistent state inside a lease window. | Fixed. Leader stickiness is not persisted, so a lease-enabled node treats its startup as leader activity and refuses to vote for one election timeout. |
| All members use the same lease setting, `LowerValue` and `ClockDriftBound`. | Operator. Startup suppression depends on the voter's own lease setting and timeout. |
| A leader may retransmit a snapshot the follower already installed, for example after a lost acknowledgment. | Fixed (#58). Any `InstallSnapshot` with `senderTerm >= Term` refreshes stickiness, steps the receiver down and records the leader, matching AppendEntries. A snapshot already covered by the committed log is acknowledged as `Replicated`/`ReplicatedWithLeaderTerm` without being reinstalled. The leader then advances `NextIndex` past it instead of retransmitting every round. |

**Red baseline.** At `3cd0336e5`, with tests from `0ab59dedb`, this command
failed 3 of 9 cases in 8 of 8 runs:

```powershell
dotnet run --project src\DotNext.Tests\DotNext.Tests.csproj --no-build -- --filter-class 'DotNext.Net.Cluster.Consensus.Raft.InProcess.LeaderLeaseTimingTests' --progress off --timeout 180s
```

- `LateLeaseTimerDoesNotExtendLease(lateTimers: True)`: "The stale leader still
  reports a usable lease after its deadline." The partitioned leader's lease
  deadline was t=200. Its timers were delayed, and node 1 was elected at t=210
  and committed index 2. The old leader still returned an uncancelled token.
- `RestartedVoterRespectsAcknowledgedLease(restart: True)`: "A restarted voter
  helped elect a new leader while the old leader's lease was still usable."
  Node 2 acknowledged a lease valid until t=249 and restarted at t=150. At
  t=210 it granted PreVote, and node 3 was elected and committed index 2.
- `RetransmittedSnapshotAcknowledgmentKeepsVoterSticky` (added later for #58):
  "A majority can elect a new leader while the lease is usable." See the
  retransmitted snapshot row above.

The controls `lateTimers: False` and `restart: False` passed at baseline.
Removing either fix makes its own test fail again.

**Not supported**

- Clock drift beyond `ClockDriftBound`, including a monotonic clock that stops
  while the process or host is suspended when other members' clocks keep
  running.
- Different lease settings, `LowerValue` or `ClockDriftBound` across members.
- Restarting a member with wiped persistent state. Such a node is a new
  member, not a restarted voter.
- A `ClockDriftBound` below 1. ASP.NET configuration rejects it, but the core
  `RaftCluster` constructor does not validate it.
- A pause between the final lease check and returning the read result. Check
  the lease again after the read; a pause after that check cannot be detected
  by the library.

The TCP/UDP `RaftCluster.NodeConfiguration` does not expose
`ClockDriftBound`, so its bound is always 1. A missing option alone does not
show a defect, so this is documented rather than changed.

**Lease acknowledgment audit (#58)**

`ReplicationProcess.ConvertToResult` counts every response except a
higher-term rejection, cancellation or unavailability toward the heartbeat and
lease quorum (`MemberResult.Touched` or `Replicated`). This is safe only if the
follower refreshed its stickiness before sending that response. After the #58
fix, that holds for the current receivers:

- AppendEntries refreshes on `senderTerm >= Term` before any check that can
  reject, including log mismatch and `UnsupportedVersion`.
- InstallSnapshot refreshes on `senderTerm >= Term` for any snapshot payload,
  including an already installed snapshot and `UnsupportedVersion`. It
  returns `Rejected` without refreshing only for a lower sender term, which
  the leader sees as a higher term, or for a non-snapshot payload, which the
  leader never sends.

The leader was not changed. Members running a version older than the fix can
still answer a retransmitted snapshot with a same-term `Rejected` that the
leader counts. Do not rely on lease reads while such members are in the
cluster, for example during a rolling upgrade.

## Follower term signal (#70)

Recorded for #70. The follower answers AppendEntries with
`HeartbeatResult.ReplicatedWithLeaderTerm` only when the request's batch holds an
entry of the sender's term (`RaftCluster.TermTracking.cs`). The leader turns that
answer into `MemberResult.Replicated(index)`, and `LeaderState.GetCommitIndex`
takes the majority of those indices. The commit path has no independent check
that the entry at the candidate index is from the current term, so the rule
against committing earlier-term entries by counting replicas (§5.4.2, Figure 8)
depends on this signal alone.

**Defect.** The follower reuses one cached `ReplicationWithSenderTermDetector`.
`Initialize` cleared only `configurationDetected`; `replicatedWithExpectedTerm`
was only ever OR-ed, and `Reset()` never cleared it. After one request contained
an entry of the sender's term, every later non-empty request answered
`ReplicatedWithLeaderTerm`, whatever its terms and whichever leader sent it.

**Impact.** Not reachable from a conforming dotNext leader:

- The candidate appends a no-op in its own term before it starts leading, and
  every barrier replicates up to `LastEntryIndex`, so each non-empty batch ends
  with an entry of the leader's term. The honest answer is already
  `ReplicatedWithLeaderTerm`. Empty batches skip the detector.
- Entries appended later carry the leader's term (`RaftClusterExtensions`, and
  the term guard for configuration entries).
- It is reachable when a sender puts no entry of its own term in a non-empty
  batch: a custom sender using the protected `AppendEntriesAsync`, or
  `ReplicateAsync<TEntry>` called with an older-term entry (rejected since #50).
- Catch-up and warm-up use `matchedIndex`, and lease and heartbeat consensus
  count `Touched` and `Replicated` alike, so neither is affected.

The defect is a protocol-contract violation by the follower, not a commit-safety
break reachable from a stock leader. No leader-side scenario test exists for the
same reason.

**Status: fixed (#70).** `Initialize` and `Reset()` clear both flags, so a cached
detector holds no per-request state. `ReplicationTermSignalTests` sends
consecutive AppendEntries requests to a follower: a batch with an entry of the
sender's term, then one with only older-term entries (same sender term, and a
later one). The second must answer `Replicated`, and a following batch with an
entry of the sender's term must answer `ReplicatedWithLeaderTerm` again.

The other sites that return `ReplicatedWithLeaderTerm` were checked and left
unchanged. The snapshot path compares `senderTerm == snapshot.Term` per request.
The local-member shortcuts in `RaftClusterMember` (core and HTTP) are never
used to replicate, because the leader replicates to itself through the base
`ReplicationProcess` and `AddMembers` asserts `IsRemote`.

`RaftCluster.ReplicateAsync<TEntry>` did not check that the entry's `Term` equals
the leader's term when #70 was fixed; #50 added that check (see below).

## Leader proposal term safety (#50)

Recorded for #50. Baseline `4dff766ed`.

**Contract**

| Surface | Term rule |
|---|---|
| `RaftCluster.ReplicateAsync<TEntry>` (also `IReplicationCluster<IRaftLogEntry>.ReplicateAsync`) and the `RaftClusterExtensions` helpers | Guarded. The entry's `Term` must equal the term of the leader state that appends it, and the log's current term under the append lock. Any mismatch, older or newer, throws `NotLeaderException` before anything is written. |
| Membership appends (`AddMemberAsync`, `RemoveMemberAsync`, automatic removal) | Guarded since #18/#47, with the same log-term check. |
| Raw `IAuditTrail`/`IPersistentState` appends (`AppendAsync` of an entry, the producer and start-index overloads, `AppendAndCommitAsync`) and the storage-level configuration append | Deliberately unguarded. Follower replication, snapshot installation, import and recovery legitimately store entries of older terms. |
| Custom `IPersistentState` | Only the best-effort pre-check (`entry.Term == state.Term`) that is not synchronized with appends; the leader-state check applies as for any log. |

**Why both checks.** The leader-state check proves that this node led
`entry.Term`. It is immutable per leadership, so it also catches a caller that
holds an old term. It cannot see a term update that happens after it. The
log-term check under the append lock catches that update, but alone it is not
enough: `UpdateTermAndStepDownAsync` writes the new term to the log before it
disposes the old leader state, and the `RaftClusterExtensions` helpers read the
term from the log. In that window a helper builds an entry of the new term while
the node is still the leader of the old one. A log-term check accepts it, and the
node would write an entry of a term it does not lead. Together the checks give the
guarantee documented for the membership path: at append time the log term is the
term this node led, and the entry is ordered before any newer-term entry. They are
not atomic with term updates, and the append can land just after the local term
advances. That is equivalent to append-then-step-down, and the next leader
truncates the entry if it was not replicated.

**Why `NotLeaderException`.** A mismatch is not a malformed argument that a caller
can fix: the helpers sample the term without synchronizing with the leadership
check, so a correct caller gets the mismatch during a transition and succeeds on
retry. The check under the append lock already throws `NotLeaderException` for the
same condition, so one condition maps to one exception. A caller that hard-codes a
wrong term while it is the leader also gets `NotLeaderException`, which is the
trade-off.

**Outcome after the append.** Once the entry is in the log, `NotLeaderException`
(leadership lost) and `OperationCanceledException` say nothing about whether the
entry commits: a leader that has it may still replicate and commit it (see #53).
Only an exception thrown before the append, including the term mismatch, means
that nothing was written.

**Red baseline.** At `4dff766ed`, `ProposalTermGuardTests` failed 4 of 7 tests:

```powershell
dotnet run --project src\DotNext.Tests\DotNext.Tests.csproj -- --filter-class 'DotNext.Net.Cluster.Consensus.Raft.InProcess.ProposalTermGuardTests' --progress off
```

- `StaleTermProposalIsRejected`: node 1 is the term-2 leader; a term-1 entry was
  appended at index 3 (the assertion expected `LastEntryIndex` 2, and got 3).
- `ProposalWithFutureTermIsRejected`: a leader appended an entry of a term it
  does not lead.
- `TermAdvanceBetweenCaptureAndAppendIsRejected`: a proposal that passed the
  leader check and waited for the append lock was appended after the log term
  advanced. The test holds the append lock with an entry that blocks in
  `WriteToAsync`, so the ordering needs no sleeps.
- `ProposalBuiltAfterTermAdvanceIsRejected`: the extension helper sampled the
  advanced log term and appended an entry of a term the node did not lead.

Passing at baseline, and kept as controls: `CurrentTermProposalIsCommitted`,
`FollowerReplicationOfOlderTermEntriesIsNotGuarded`, and
`OverwrittenProposalNeverSucceeds`.

**Appended versus committed.** In a probe at the baseline, the stale term-1 entry
in the term-2 leader's log was appended at index 3 and replicated to both
followers, but the commit index stayed at 2 and `ReplicateAsync` stayed pending
for the 3 s that the probe pumped replication. The followers answered
`Replicated` because the batch held no entry of the leader's term (#70). So the
baseline showed an appended and replicated stale entry, not an acknowledged one.
The probe is not kept as a test.

**Overwrite of an awaited index.** `ReplicateAsync` waits for `WaitForApplyAsync(index)`,
which does not compare terms. `OverwrittenProposalNeverSucceeds` appends at index
2 on the term-1 leader, keeps it unreplicated, elects another leader whose no-op
overwrites index 2, and waits until the old leader has applied the overwrite. The
old `ReplicateAsync` throws `NotLeaderException`: the old leader steps down when it
sees the newer term, which cancels its leader token before any AppendEntries of
the new leader can overwrite or commit the index. The test passed at baseline, so
no term check was added after the apply.

## Snapshot install cancellation (#73)

PR #62 (#53) made routine cancellation of a normal append leave the WAL usable
and explicitly left the snapshot path open. This closes that gap.

**Defect.** `RaftCluster.InstallSnapshotAsync` passes a token that combines the
transport request token with the cluster lifetime to
`WriteAheadLog.AppendAsync(snapshot, ...)`. The WAL set `mutationStarted` before
`IStateMachine.ApplyAsync(snapshot, token)` and faulted itself on any exception
from it, including `OperationCanceledException`. A leader that dropped a slow
request therefore poisoned the follower's WAL until it was reopened, although the
WAL itself changes nothing before `ApplyAsync` returns (`WriteSnapshotBoundary`,
`LastCommittedEntryIndex` and `PersistAppendAsync` all follow it).

**Contract.** `IStateMachine.IsSnapshotInstallCancellationSafe` is an opt-in
default interface member (default `false`). When it is `true`, an
`OperationCanceledException` from `ApplyAsync` of a snapshot entry, thrown while
the passed token is canceled, means the state machine made no observable change.
The WAL then leaves itself untouched, does not fault, and rethrows. Anything else
stays fail-closed: any other exception, an `OperationCanceledException` while the
token is not canceled, and any state machine that has not opted in.
Implementations that opt in but restore under the request token risk a partially
restored state that the WAL treats as routine.

**`SimpleStateMachine`** opts in.

- The transfer (`Snapshot.ReadFromAsync`) writes to a temporary file and observes
  the request token. On failure the temporary file is deleted, and the
  `{index}-{term}` snapshot file is created only by `Commit()`, so a cancelled
  transfer never becomes the snapshot and is never picked up on restart.
- `EndSnapshottingAsync(commit: false)` only rolls back a local snapshot in
  progress and clears it, so repeating it on the retransmission is a no-op. A
  local snapshot that has itself failed is dropped (see
  [Failed background snapshot](#failed-background-snapshot-75)). If it was
  canceled by disposal, the cancellation is rethrown as
  `InvalidOperationException` and the WAL fails closed instead of treating it as
  routine.
- `RestoreAsync` runs under the state machine's lifetime token, not the request
  token. Once it starts the state can be partially rebuilt, so a cancelled request
  must not interrupt it. An `OperationCanceledException` from the restore (only
  possible on disposal or from user code) is rethrown as
  `InvalidOperationException`, so the WAL sees it as a real failure and fails
  closed rather than as a routine cancellation.

**Not changed.** A restore failure still faults the WAL. A local storage error
while staging the incoming snapshot also stays fail-closed. Built-in transports
now cancel the request when its payload source fails (#90), so their connection
errors and truncated payloads take the safe cancellation path described above.
A custom transport must provide the same cancellation signal; an arbitrary
non-cancellation exception from its payload source still faults the WAL.

**Alternatives.** A WAL-owned staging area for the snapshot would remove the
contract, but it is a larger change to the state machine interface. Dropping the
caller token would stop cancellation of a stuck transfer.

**Tests.** `WriteAheadLogSnapshotCancellationTests` (transfer cancelled, cancelled
after restore started, restore failure, and OCE from restore),
`WriteAheadLogAppendFailureTests` (the WAL-level contract and the fail-closed
cases) and `SnapshotInstallCancellationTests` (the production path in the
in-process harness). The in-process test sends the snapshot with a token it owns
through the network to `RaftCluster.InstallSnapshotAsync`, because the token of a
request dispatched by the leader's worker cannot be cancelled deterministically.

## Snapshot transfer failure (#113)

Issue #113 revisited whether every exception before snapshot restoration should
be recoverable. The transport case had already been fixed by #90 / PR #98:
`PayloadSourceScope` converts a TCP or HTTP payload-source failure into
cancellation of the request. For an opted-in state machine, the #73 contract
then rolls back the temporary snapshot, leaves the WAL unchanged and lets the
leader retransmit. `ProtocolInputBudgetTcpTests` and
`ProtocolInputBudgetHttpTests` cover disconnects and truncation for snapshot
payloads; `PeerDisconnectMidSnapshotAllowsSimpleStateMachineRetransmission`
additionally uses the real two-phase `SimpleStateMachine`, checks that its
temporary file is removed and installs the retransmission.

The remaining recoverable designs were rejected:

| Option | Decision |
|---|---|
| Generalise the opt-in contract | Rejected. A marker exception would add public API for local I/O and custom transports only. A new boolean or a redefinition of `IsSnapshotInstallCancellationSafe` cannot distinguish transfer from restore and would make restore errors unsafe. |
| WAL-owned staging | Rejected. `SimpleStateMachine` already stages the snapshot, so this would write every snapshot twice and change the streaming contract of `ApplyAsync`. |
| Transport buffering | Rejected. #90 already converts source failures without buffering; buffering a snapshot would conflict with the bounded-allocation goal of #22. |
| Keep fail-closed | Chosen for local storage and arbitrary non-cancellation failures. Built-in transport failures remain recoverable through #90. |

A local failure before restore, such as disk full or a temp-file write or flush
failure, stays fail-closed. It is evidence that the node cannot durably stage the
snapshot, consistent with WAL storage errors and a failed
`SnapshotWriter.Commit()` (#75). Retrying inside the WAL would not make progress:
the leader would repeatedly stream the entire snapshot to the same unhealthy
storage. A bounded retry would still escalate to fail-closed and would add policy
and public configuration without improving the follower's ability to participate
in consensus. The existing background-failure path therefore remains the visible
signal; no new log event or metric is added.

Custom transports should give `RaftCluster.InstallSnapshotAsync` a request token
whose source they control, cancel it before surfacing a payload-read failure and
report cancellation from the payload. Local storage failures and failures after
restore starts must not be translated. The XML contract documents this rule.

## Failed background snapshot (#75)

**Defect.** `SimpleStateMachine` runs a snapshot in the background
(`BeginSnapshottingAsync`) and keeps its task in `snapshottingProcess`. When the
snapshot failed (a persist or serialization error, or a cancellation), the task
stayed faulted or cancelled in that field. The next apply, and every incoming
snapshot install, awaited it before clearing the field, so the await threw first
and the field was never cleared. The WAL applier turned that into a background
failure, so one failed snapshot made the node fail closed until it was reopened.

**Fix.** `InstallSnapshotAsync(Task)` and `RollbackSnapshotAsync` catch a failure
of the snapshot task, clear the field with a compare-exchange against that same
task, and do not rethrow it. The writer is already disposed and rolled back by
`BeginSnapshottingAsync`, so there is nothing to publish. The previous snapshot
stays the current one, and the next persist point starts a new attempt. The
failure is not swallowed: the compare-exchange winner calls the new protected
virtual `OnSnapshotFailed(Exception)` once (a new public-surface member; it is a
no-op by default, and `SimpleStateMachine` has no logger to use instead). An
exception thrown by the hook propagates like any other apply failure.

**Not dropped.**

- Cancellation by disposal. The filter `!lifetimeToken.IsCancellationRequested`
  keeps today behaviour: the failure is rethrown, and nothing is reported.
- A failure of `writer.Commit()`. The field is cleared before it runs and the
  failure is rethrown, so it stays fail-closed. Commit renames the temporary file
  into place, so a failure can mean a half-renamed or unverifiable snapshot
  file, and continuing over it would hide that.
- A failed `Rollback()` (#81). It runs only when a snapshot from the leader
  supersedes an unpublished local one, so nothing was published and a failure
  leaves at most a stray `*.tmp` file or an unflushed delete. Before #81 the
  failure was rethrown and `snapshottingProcess` stayed set, so a later apply
  could commit the writer that was already rolled back. Now the field is cleared
  and the failure is reported through `OnSnapshotFailed` once, like any other
  dropped snapshot, and the incoming snapshot installs. Failing the WAL over a
  failed cleanup was judged disproportionate; a subclass that wants that can
  throw from `OnSnapshotFailed`. Guard:
  `RollbackFailureIsReportedAndRolledBackWriterIsNotCommitted`.

**Alternatives.** Retrying the snapshot inside the state machine would change the
snapshot trigger policy, which is out of scope. Dropping the failure with no hook
would make it unobservable.

**Tests.** `SimpleStateMachineSnapshotFailureTests` (faulted and cancelled
failures: applies continue, a later snapshot is published, an incoming snapshot
installs through the WAL, a restart restores the last good snapshot, and the hook
reports each failure once; plus a guard that disposal in flight is unchanged).
Two tests that asserted the old poisoned behaviour were updated:
`SimpleStateMachineSnapshotTests.FailedOutgoingSnapshotIsRolledBack` and
`WriteAheadLogSnapshotCancellationTests.CancelledBackgroundSnapshotDoesNotBlockSnapshotInstall`.

## Term/vote durability (#24)

**Question.** Is the term/vote record (`WriteAheadLog.NodeState`, S5) durable
before it is acted on, within the process-crash model?

**Crash model used.** A dispose and reopen of the WAL at the same location, then
a restart of the node. Fault injection swaps the private `state` file handle for
a read-only handle to the same file (`NodeStateFault`, test-only reflection; no
runtime seam for record writes was added). Power loss is out of scope and is documented in
`src\cluster\README.md` (Supported storage and crash model).

**Hypotheses.**

| # | Hypothesis | Result |
|---|---|---|
| H1 | A vote is granted, or a reply carries `Value = true`, before it is durable | Not reproduced. The handler sets `Value = true` only after `UpdateVotedForAsync` returns. Guards: `FailedVoteWriteGrantsNothing`, `CancelledVoteRequestGrantsNothing`. |
| H2 | After a failed flush, memory is ahead of disk and the node grants X, then Y in the same term after a restart | Not reproduced. A failed request was never granted; a retry rewrites the whole record; before a restart the node only refuses the other candidate (availability, not safety). Guards: `FailedVoteWriteNeverYieldsTwoGrantsInOneTerm`, `RecoveredVoteWriteKeepsTheGrantAcrossRestart`. `FailedWriteThatReachedTheDiskIsNotOverwrittenByLowerTerm` guards the ambiguous-failure path. |
| H3 | A term is advertised in an RPC reply before it is durable | **Reproduced.** See below. |
| H4-H7 | Sector atomicity, directory fsync, `WriteThrough` mapping, short `state` file | H5 fixed in #82 (directory fsync after creating `state`, retried on reopen); H7 fixed in #83 (a `state` file of 1 to 36 bytes fails closed); H4 and H6 are documented only (power-loss assumptions). |

**Defect (H3).** `IncrementTerm`, `UpdateTerm` and `UpdateVotedFor` changed the
in-memory term and vote before `FlushAsync`. When the flush failed, memory stayed
ahead of disk. A steady follower does not rewrite `state` (`StepDownAsync`
persists only when the new term is higher than the current one), so after the
error cleared, the next `AppendEntries` in the new term was acknowledged at a
term that was never persisted. After a restart the node came back at the old
term, and a stale leader of that term could overwrite an entry the node had
acknowledged. Triggers: a failed `UpdateTermAsync` from an `AppendEntries` or
from a `Vote` request of a higher term.

**Evidence.**

- Baseline SHA: `b7f39ab4085963e425a24bfe017119ca1d611e2f` (`origin/fork`), with
  only the new tests applied.
- Command: `dotnet run --project src\DotNext.Tests\DotNext.Tests.csproj -- --filter-class 'DotNext.Net.Cluster.Consensus.Raft.InProcess.TermVoteDurabilityTests' --progress off`
- Expected: after a restart the durable term equals the acknowledged term
  (`AcknowledgedTermSurvivesRestart`), and a stale-term `AppendEntries` does not
  overwrite the acknowledged entry (`StaleLeaderCannotOverwriteAcknowledgedEntry`).
- Observed on baseline: 9 tests, 5 passed, 4 failed (both H3 tests, each for the
  `AppendEntries` and `Vote` triggers). H1, H2 and the no-fault control passed.
- Observed with the fix: 10 tests (9 plus the ambiguous-failure guard), 10 passed.

**Fix (persisted format unchanged).** Publish after durable. `StageTerm`,
`StageIncrementedTerm` and `StageVote` fill the 37-byte buffer with the complete
next record without touching the published fields; the `IPersistentState`
method awaits `FlushAsync` under `stateLock`, and only then does `Publish()` set
the in-memory term and vote. A failed or cancelled flush leaves the published
state equal to what was last acknowledged. If the bytes did reach the disk
anyway (an ambiguous failure), the disk is ahead of memory. A failed write marks the
record suspect and immediately reads it back and publishes it (what is on disk is durable),
so RPCs do not keep acting on an older term. If that read fails, the next term/vote
operation first reads the record back under `stateLock`. If it differs from what
was published, that operation fails with an `IOException` and the caller
re-evaluates on fresh state; otherwise it proceeds. Without this, a later write
derived from the stale published term could lower the durable term. If that read-back
fails, the operation fails and nothing is written. Residual gap, not fixed: while
the record is suspect (failed write that reached the disk, then a failed read of the
same file), an `AppendEntries` at the old published term performs no write and can
still be acknowledged. Making RPCs fail closed in that state was judged out of scope
for an investigation PR; it needs broad storage failure to occur.


**Alternatives rejected.** Poisoning the WAL on a `state` write failure (it
conflicts with the cancellation handling of #53). Raising the term from the last
log term at startup (changes recovery semantics and does not cover replies that
were already sent).

**H7 (#83).** The `NodeState` constructor now fails closed on an existing `state`
file of 1 to 36 bytes: it throws `IntegrityException` naming the file and the
manual recovery, and does not touch the file. Length 0 is initialized as before
(the only length reachable in the process-crash model), and length 37 or more is
unchanged. `WriteAheadLogNodeStateTests` covers lengths 1, 18 and 36 (red before
the change) and the two unchanged cases. A *missing* `state` file next to an
existing checkpoint is still recreated at term 0; that is not changed here.

**H5 (#82).** The constructor now calls `DurableFile.FlushDirectory` whenever it
opens `state`, including immediately after creation. The helper already existed
(`LibraryImport`, so AOT-safe; on Windows it flushes a directory handle, not a
no-op). On a fresh WAL the checkpoint constructor already flushed the root
directory right after `state` was created, so first boot was covered by accident;
the explicit call also covers a `state` created next to an existing checkpoint.
If the barrier fails, the constructor closes the handle; reopening repeats the
barrier independently of whether cleanup could remove the file.
`WriteAheadLogNodeStateTests` injects that failure through an internal constructor
seam and verifies the retry; `DurableFileTests` exercise the native helper on the
current OS. The barrier runs only after the full 37-byte initial record has been
written, so a file left by a failed barrier is reopened as a complete record and
the barrier is retried; it is not mistaken for a truncated record. A power-loss
reproduction remains out of scope.

*Directory-entry flush inventory.*

| Create/rename site | Directory flush |
|---|---|
| WAL data and metadata pages | Flushed before each checkpoint write (`Persistence.cs`, `Flusher.cs`) |
| Checkpoint creation and publication | Flushed (`Checkpoint.cs`, `DurableFile.Publish`) |
| Snapshot `Commit()` rename and `Rollback()` | Flushed (`DurableFile.FlushPublication`, `FlushDirectory`) |
| Applied configuration baseline (`PersistentClusterConfigurationStorage`) | Flushed after every atomic publication (`DurableFile.Publish`, #106); reopening an existing file or using an instance after a failed post-rename barrier repeats the full publication barrier. |

## Seeded simulation, #56 stage 1

Part of #56. `SimulationTests` (see the in-process README) adds a seeded schedule runner over the existing in-process
harness with WAL-backed nodes, restarts, and three safety oracles plus a separate liveness check.
It is test-only; no production code changed.

**Scope.** 3 and 5 voters, no membership changes. Faults: message drop, hold/deliver, lost responses, partitions, manual
time, node crash and recovery. Oracles: election safety, committed-prefix agreement, acknowledged writes preserved.

**Checker validation.** Each oracle has a synthetic bad-history test. Mutation runs, local only and not committed:
skipping the state flush when a vote is granted was found by the campaign in 3 of 3 runs (150 seeds each, election
safety, after 40 to 130 s), and by none of the 16 fixed seeds. The #24 publish-after-durable change could not be
mutated meaningfully: it only differs when the state write fails, which needs I/O fault injection (out of scope);
a publish-before-flush approximation was not detected by the simulation or by `TermVoteDurabilityTests`.

**Residual blind spots.** Membership changes, linearizable reads and read barriers (#65), leases, snapshots and compaction,
I/O faults (#24), real transports, and process kill. Thread-pool scheduling and the wall-clock settle heuristic are not controlled,
so a seed does not guarantee replay: a seed that failed in a campaign passed on three replays. Election timeouts are fixed per node,
the schedules are short, and the fixed CI seeds find little alone; the campaign is where faults are found.
Interleavings that need many elections (#49, #70) depend on the campaign reaching them.
Crash points are between steps only, never inside a write.

## Durable-write load baselines, #118 stage 2

Part of #118. [`src/DotNext.Benchmarks.DurableWrite`](src/DotNext.Benchmarks.DurableWrite/README.md) is a Release
workload CLI. It drives a `WriteAheadLog` on its own, and `RaftCluster` with 1, 3 and 5 voters over real loopback TCP in
one process. It records baselines as JSON and fails only on correctness oracles. No production code changed.

**Commands.** `--profile smoke` takes about 2 minutes (9 cells) and runs in CI on every change to the WAL, Raft or the tool,
followed by the three `--inject` modes. `--profile full` takes about 15 minutes (37 cells) and runs locally or through
`workflow_dispatch` (`.github/workflows/durable-write-load.yml`), which uploads the JSON as an artifact.

**Tool shape.** A new project, not `DotNext.Benchmarks.WAL` (which keeps its FASTER comparison) and not BenchmarkDotNet,
whose many-iteration, mean-of-means model hides queueing, tail latency and the oracles. Replicated cells use the
public TCP transport rather than `InProcessNetwork`: the in-process harness needs internals that are visible only to
Debug test builds, and it skips serialization and sockets. The cost is that all nodes share one process, CPU and device.

**Durability.** `FlushInterval = 0`, `ChunkSize = 4 MiB`, shared memory (private, and private with `NoBuffering`, in two
cells per size). The acknowledgment is durable because `WriteAheadLog.PersistAppendAsync` flushes the data and metadata
pages, both directories and the checkpoint before it publishes `LastEntryIndex`, under `persistenceLock`. A follower replies
only after that, and `ReplicateAsync` returns after a majority replied and the leader applied. Three checks show the numbers
are not buffered writes: every node is reopened after the cell and a majority must recover every acknowledged write at its
index (durability oracle); each node's recovered log must hold every entry its own state machine applied, and every index
its log applied past the end of the reported history must be a no-op (history reconciliation, which also catches a silently
lost last apply that no later callback exposes); and an
fsync probe on the work volume marks a cell `suspectBuffered` if its durable p50 is below half a 4 KiB `Flush(true)`. The
per-node flush/append ratio is reported for information only: the WAL meter counts the background flusher's flushes too,
so it cannot prove that each append was flushed.

**Oracles.** An online checker sees every apply and snapshot install on every node during the workload: apply order
(contiguous, no duplicates, per-client order), committed-prefix agreement, acknowledged writes applied, and election
safety through `SimulationHistory`. At the end, `SimulationHistory` checks the final prefixes and acknowledged writes again.
The state machine snapshot carries the whole applied history, so the oracles hold through compaction and `InstallSnapshot`,
which the #56 stage 1 simulation could not cover. The slow-follower cells (5 ms relay delay, a 5 s pause at 30% of the run,
a snapshot every 200 or 500 entries) make the follower install 4 to 9 snapshots while the load continues; a slow-follower
cell in which the follower installs no snapshot is a liveness failure (exit 4), since it did not exercise compaction.

**Checker validation.** `--inject` runs a 3-voter cell with a test-only failure, and CI requires exit code 3 with the
expected oracle. Dropping the apply of index 100 on a follower is caught by apply order (`node 1 skipped index 100, which
node 0 applied as 'm0-c4-s13'`). Applying index 100 after index 101 is caught by committed-prefix agreement. Followers that
acknowledge without writing (`ConsensusOnlyState`, the "skip the flush before the ack" case) are caught by the durability oracle
(`'m0-c7-s1' was acknowledged by node 0 at index 5, but only 1 of 3 voters recovered it from storage; a majority is 2`).
`DurableWriteOracleTests` (28 tests) checks each oracle against synthetic good and bad histories.

**Bounds.** Fixed cells and durations; every cell also stops at 200,000 acknowledged writes or 2 GiB of payload; the run
stops at 30 minutes. Cells are skipped below 2 GiB free on the work directory's volume and stopped below 1 GiB; a run cut
short by either bound exits 5 (incomplete) rather than 0. Data is deleted after each passing cell, and CI uploads it as an
artifact when a job fails.
Loopback only, no privileges, no cache dropping. No latency, throughput or memory thresholds.

**Baseline** (2026-10-05, revision 3dd6faf5; i9-14900K x32, 128 GiB, Windows 10.0.26300, NTFS on a local fixed disk,
.NET 10.0.12 workstation concurrent GC, shared memory). Raw report:
`src/DotNext.Benchmarks.DurableWrite/baselines/windows-i9-14900K-2026-10-05.json`; full table and a second run for
spread in the tool's README. The fsync probe measured a flushed 4 KiB write at p50 497 µs and p99 987 µs, against 5 µs
buffered. Every oracle passed in all 37 cells (the report predates the history reconciliation); no cell was `suspectBuffered`; CPU stayed at or below 0.47 cores, so the
cells are I/O-bound.

| Cell | acked/s | ack p50 | ack p99 | ack p99.9 |
|---|---:|---:|---:|---:|
| `wal-append-128B` c1 / c16 / c64 | 70 / 137 / 189 | 10 / 93 / 306 ms | 82 / 280 / 610 ms | 91 / 299 / 1128 ms |
| `wal-batch-128B` b16 / b256 (entries/s) | 1,660 / 16,441 | 9.3 / 12 ms per batch | 14 / 84 ms | 17 / 91 ms |
| `raft-closed-1v-128B` c1 / c16 / c64 | 100 / 95 / 47 | 9.5 / 81 / 1253 ms | 19 / 588 / 2507 ms | 49 / 633 / 3589 ms |
| `raft-closed-3v-128B` c1 / c16 / c64 | 51 / 167 / 189 | 18 / 92 / 340 ms | 74 / 169 / 625 ms | 94 / 191 / 698 ms |
| `raft-closed-3v-16KiB` c1 / c16 / c64 | 58 / 168 / 202 | 17 / 90 / 312 ms | 23 / 160 / 594 ms | 29 / 206 / 651 ms |
| `raft-closed-5v-128B` c1 / c16 / c64 | 49 / 149 / 184 | 20 / 103 / 344 ms | 26 / 177 / 602 ms | 28 / 199 / 690 ms |
| `raft-open-3v-128B` 50% / 90% / 120% of 189/s | 95 / 170 / 140 | 37 / 70 / 5243 ms | 66 / 162 / 9634 ms | 73 / 185 / 9744 ms |
| `slow-follower` 3v / 5v, c16 | 180 / 141 | 85 / 102 ms | 146 / 178 ms | 161 / 2081 ms |

The slow follower installed 9 (3 voters) and 8 (5 voters) snapshots through compaction, and every node recovered every
entry. Per node, 128 B entries cost 1.25 appended bytes per payload byte and 16 KiB entries 1.002. These are baselines,
not thresholds. Observations for follow-up, none of them a correctness failure:

- unbatched WAL appends barely scale with concurrency, because each `AppendAsync` flushes under one lock, while batched
  appends reach 4,000 to 16,000 entries/s;
- a single-node cluster gets slower with more clients (100/s at c1, 47/s at c64);
- at 120% offered load the leader held up to 1,510 uncommitted entries, went through 3 terms, and 2,133 writes ended with an
  unknown outcome; the oracles still held;
- run-to-run spread is large for some cells (`raft-closed-3v-16KiB-c1`: 6/s in one full run, 58/s in the next).

**Residual blind spots.** Power loss and page-cache loss (the recovery audit reopens the files in the same OS instance);
process kill and crash mid-append; cross-process isolation (CPU and GC are per process, not per node); the HTTP and UDP
transports and real networks; membership changes and I/O faults under load; leader-side batching of client proposals,
which the public API does not expose. A baseline is one machine, filesystem and device; compare runs on the same host
only. Stage 3 of #118 covers real-process fault and burn-in campaigns.

## Durable-write latency investigation, #123

Part of #123: measurement only. It changes no durability semantics, default setting or code path. The questions (Q1–Q4)
and hypotheses (H1–H4) are those of the issue.

**Sources, and what each can attribute.**

| Source | Attributes | Cannot attribute |
|---|---|---|
| `DotNext.IO.WriteAheadLog` meter (existing counters and `checkpoint-flush-duration`) | entries appended, committed and flushed per node; the duration of the whole flusher cycle | the append path's persist cycle; any phase; lock wait or hold |
| Raft meters (existing: `broadcast-time`, `response-time`, transitions) | the leader's broadcast round duration; the response time per message type | why a round was slow; the gap between rounds or between heartbeats |
| New opt-in instruments, this PR (below) | the persist cycle per cause (append or flush) and its six phases; wait and hold of the persistence lock; wait of the WAL lock per cause | which fsync inside a phase was slow; the device queue |
| strace `-f -c` differential (Linux CI, no privileges) | durability system calls per acknowledged write: `fsync`, `msync`, `rename`, `unlink` | whether the kernel sent a device flush |
| `/proc/diskstats` (Linux) | device write and flush requests in the window | per process: it is system-wide |
| `GetProcessIoCounters` (Windows) | bytes and write operations of the process | flushes: `otherOps` mixes them with every other control call, so it is an upper bound only |
| The tool's `MetricsCollector` and new per-second timeline | acks, backlog, the longest broadcast round and follower election-timer refresh gap, the longest lock waits and the transitions, per second, against the leader claims and their terms | causality inside one second; a leader message from a same-term vote grant (both reset the timer) |

ETW and `dotnet-trace` need a session that this host could not open without administration (ETW) or the global tool
(not installed); they were not needed: the in-process listener reads the same EventSource. The lock wait against hold
time and the time of each phase are not observable from outside the process, so they need production
instrumentation. The smallest that answers them, and the one added:

- `WriteAheadLog.Diagnostics.cs`: three histograms on the existing `DotNext.IO.WriteAheadLog` meter
  (`persist-phase-duration`, tagged with phase and cause; `lock-wait-duration`, tagged with lock and cause;
  `lock-hold-duration`) and an EventSource `DotNext-IO-WriteAheadLog` (keyword `0x1`) with the same three events.
- Cost when nothing listens: an `Instrument.Enabled` and an `EventSource.IsEnabled` check per lock acquisition and per
  persist cycle, and a null check per phase; no timestamp and no allocation. The per-log trace state is allocated once,
  with the log.
- The WAL tests (410) pass unchanged; `WriteAheadLogDiagnosticsTests` checks that the instruments report every phase
  and cause and stay silent without a listener.

The tool's `--diagnostics` subscribes to them and adds `cells[].diagnostics` to the JSON (schema 3); `--repeat` and
`--cells` repeat a subset. Without `--diagnostics` the cells, settings, oracles and exit codes are unchanged
([README](src/DotNext.Benchmarks.DurableWrite/README.md#diagnostics---diagnostics---repeat---cells-123)).

**Runs.** Windows: i9-14900K x32, Windows 10.0.26300, NTFS on a local fixed disk, .NET 10.0.12; the full profile
with `--diagnostics` (revision 5c2ce67a, all 37 cells, every oracle passed) and a repeat run (below). Linux: GitHub
`ubuntu-24.04` runner, AMD EPYC 7763 x4, ext4 on `/dev/root` (Azure managed disk), .NET 10.0.12; the full profile with
`--diagnostics` (revision f48b8657, every oracle passed), a strace differential on two cells, and a repeat run. The
sync probe on the two hosts:

| Probe (p50 / p99) | Windows NTFS | Linux ext4 |
|---|---:|---:|
| 4 KiB write + `Flush(true)` | 480 / 987 µs | 389 / 789 µs |
| directory flush | 507 / 889 µs | 13 / 35 µs |
| publish (write, flush, rename, directory flush) | 1,959 / 6,511 µs | 671 / 1,051 µs |
| delete + directory flush | 595 / 907 µs | 337 / 469 µs |

### What one persist cycle costs (Q1)

`PersistAppendAsync`, and the flusher after it, run the same cycle under `persistenceLock`:

1. `pages`: write and flush the dirty data and metadata pages (2 `msync` + 2 `fsync` on Linux);
2. `data-directory` and `metadata-directory`: `FlushDirectory` twice (2 `fsync`);
3. `checkpoint-intent`: publish a 64-byte intent record: write a temporary file, flush it, rename it, flush the
   directory (2 `fsync`, 1 `rename`);
4. `checkpoint-slot`: write the 64 KiB checkpoint slot and flush it (1 `fsync`);
5. `checkpoint-commit`: delete the intent and flush the directory (1 `fsync`, 1 `unlink`).

That is **8 `fsync`, 2 `msync`, 1 `rename` and 1 `unlink` per cycle**, and 64 KiB written for the slot whatever the
entry size. The strace differential (15 s minus 5 s runs of the same cell on Linux) confirms it exactly:

| Linux cell | fsync / ack | msync / ack | rename / ack | unlink / ack | cycles / ack |
|---|---:|---:|---:|---:|---:|
| `wal-append-128B-c1` | 16.0 | 4.02 | 2.0 | 2.0 | 2 |
| `raft-closed-3v-128B-c1` (3 nodes, one process) | 31.7 | 7.96 | 3.96 | 3.98 | 4 |

On Windows the process wrote 131 KB per acknowledged 128-byte write in `wal-append-128B-c1` (4 write operations, two
64 KiB slots), so the write amplification at the device is about 1,000 times the payload, which the tool's
`writeAmplification` (log bytes only) does not show.

Phase durations at p50 (p99), `wal-append-128B-c1`, append cycle:

| Phase | Windows NTFS | Linux ext4 |
|---|---:|---:|
| pages | 1.18 (1.64) ms | 0.37 (0.89) ms |
| data-directory | 0.19 (0.51) | 0.01 (0.02) |
| metadata-directory | 0.17 (0.46) | 0.01 (0.01) |
| checkpoint-intent | 2.16 (3.13) | 0.56 (1.39) |
| checkpoint-slot | 0.68 (1.03) | 0.30 (0.61) |
| checkpoint-commit | 0.69 (1.23) | 0.27 (0.50) |
| **cycle (mean)** | **5.2 ms** | **1.61 ms** (flush cycle 1.21: its pages are already clean) |

**Answer to Q1.** A write costs two cycles of 8 `fsync` each, not one fsync: 16 fsync-equivalents per
acknowledged write on one node, and 32 across a 3-voter cluster at c1. On Windows the checkpoint accounts for 3.5 of the
5.2 ms of a cycle, and the intent alone (a create-flush-rename-flush) for 2.2 ms; the two directory flushes cost 0.36 ms,
less than the 1 ms the probe suggests, because the directory is often clean. Two cycles of 5.2 ms are the 10.3 ms p50 of
`wal-append-128B-c1`. A batch of 16 pays the same two cycles, which is why it also takes about 10 ms.

Caveat on Linux: `/proc/diskstats` counted 0 device flush requests against 16 `fsync` calls per acknowledged write (run
37322321254, partition `sda1`: 15.1 device write requests per ack on `wal-append-128B-c1`, 30.4 on
`raft-closed-3v-128B-c1`). The
runner's disk (`sda`) reports `write through` in `/sys/block/sda/queue/write_cache` (logged by the strace step,
run 37316770014, which also reproduced 16.01 `fsync` and 4.02 `msync` per ack): it declares no volatile write cache, so
the kernel sends no flush command and `fsync` costs only the journal write. Linux CI absolute times are therefore not
those of a consumer NVMe device with a volatile cache; the cycle structure and the ratios are.

### H1: two serialized persist cycles per write — confirmed

| Cell, c1 | Windows | Linux |
|---|---|---|
| `wal-append-128B-c1` | 95.7/s, p50 10.3 ms; append 1.000 + flush 0.999 cycles/ack | 345/s, p50 2.8 ms; append 1.000 + flush 1.000 cycles/ack (1.61 + 1.21 ms/ack) |
| `raft-closed-1v-128B-c1` | 97.6/s, p50 10.0 ms; 1.000 + 0.990 | 392/s, p50 2.5 ms; 1.000 + 1.000 |
| `raft-closed-3v-128B-c1` | 56.4/s, p50 17.3 ms (repeat median); leader 1.00 + 0.99 cycles/ack, each follower 1.00 + ~0.01 | 231/s, p50 4.2 ms; leader 1.00 + 0.95, each follower 1.00 + ~0 |

At c1 the applier sets the flusher's trigger after each apply, and the flusher runs a full second cycle to make the commit
index durable. The two cycles serialize on `persistenceLock`: in `wal-append-128B-c1` on Linux the append waits a p50
of 1.2 ms for the flusher, and the flusher 1.6 ms for the next append. On Windows the 3-voter c1 path is sequential:
the leader's append waits 5.2 ms for the previous write's flush cycle, runs its own 5.1 ms cycle, and then the
followers run theirs (7.2 ms: two followers flushing at once on the same device slow each other), which adds up to the
17.3 ms p50. A follower has almost no flush cycles at c1, because
the next append's checkpoint already carries the commit index it learned. At c16 and above the flush cycles fall to
0.002 to 0.07 per acknowledged write on both hosts: the trigger coalesces while the lock is busy.

### H2: no group commit for concurrent appends — confirmed

In every unbatched cell the leader (or the lone WAL) runs 1.00 append cycles per acknowledged write and writes one entry
per cycle, at any concurrency. Throughput therefore plateaus at one cycle time:

| Cell | Windows c1 / c16 / c64 | Linux c1 / c16 / c64 |
|---|---|---|
| `wal-append-128B` | 96 / 183 / 151 | 345 / 639 / 661 |
| `raft-closed-1v-128B` | 98 / 192 / 164 | 392 / 651 / 677 |
| `raft-closed-3v-128B` | 56 / 147 / 157 | 231 / 608 / 665 |
| `raft-closed-5v-128B` | – / 143 / 155 | 205 / 570 / 649 |
| ceiling, 1 / append cycle | ≈ 190/s | ≈ 690/s |
| `wal-batch-128B` b16 / b256 (entries/s) | 842 / 4,681 | 5,905 / 72,222 |

An explicit batch writes 16 or 256 entries per cycle (0.062 and 0.004 cycles per entry), which is where the throughput is.
Followers do group: one AppendEntries carries every entry the leader appended since the previous round, so a follower
writes 7.6 entries per cycle at c16 and 31.6 at c64 on Linux (31.5 on Windows). That is why a 3-voter cluster reaches the
single-node ceiling rather than staying below it: the follower cycles are amortized, and only the leader runs one cycle
per write. `RaftCluster.ReplicateAsync` appends one entry per call, so a client cannot reach the batch path.

### H3: commit flushes contend with appends (Q2) — refuted as stated; the cause is lock queueing

Commit flushes do not compete at high concurrency: at c16 and c64 they run 0.001 to 0.004 times per write on a single
node, and the append path's `persistenceLock` wait is 0 at p50. The persistence lock is not the queue. The queue is the
WAL append lock, which the append holds across its whole persist cycle, and the WAL read lock that the flusher, the
applier and the log reader need:

| Linux, `wal-append-128B` | c16 | c64 |
|---|---:|---:|
| append-lock wait, p50 | 11.8 ms | 48.8 ms |
| flusher read-lock wait, p50 | 17.7 ms | 86 ms |
| persistence-lock wait (append), p50 | 0 | 0 |

The append-lock wait is the queue length times the cycle (c64: 64 × 1.45 ms / 2 ≈ 46 ms). The read lock is compatible
with an append, but `QueuedSynchronizer` drains its wait queue strictly in order and stops at the first waiter it cannot
grant, so a read request queued behind an append waits for every append ahead of it. On Windows the flusher's read wait
is 66 ms at c16 and 322 ms at c64, and the c64 ack p99 reaches 1.4 to 1.6 s on one node.

**Answer to Q2.** A single node does not get systematically slower with clients. With diagnostics on, Windows measured
98 / 192 / 164 per second at c1 / c16 / c64, and Linux 392 / 651 / 677. Its throughput is capped by H2 (one cycle per
write), and its tail grows with the FIFO queue. The baseline's 95/s at c16 and 47/s at c64 are not reproduced in the
same matrix; they are run-to-run spread (Q4).

### H4: heartbeats and replication delayed behind the append path (Q3) — confirmed

At 120% open-loop load the leader's broadcast round, which also carries the heartbeat, reads the entries it sends under
the WAL read lock, so it queues behind every pending append (the FIFO order above). On Linux the leader's read-lock wait
was 230 ms at p50 and 2.59 s at p99, and its append-lock wait 1.33 s at p50. The rounds stretch with the queue (about the
queue depth times the 1.4 ms cycle), and once a round exceeds the followers' election timeout (1,000 to 2,000 ms) they
start an election.

| `raft-open-3v-128B-120pct` | Windows | Linux |
|---|---:|---:|
| offered / completed per second | 188.8 / 164.5 | 798.5 / 145.7 |
| ack p50 / p99 | 1.12 s / – | 4.7 s / 6.5 s |
| unknown / overloaded outcomes | 0 / 0 | 6,707 / 4,327 of 15,977 |
| largest uncommitted backlog | 585 | 4,095 (the tool's in-flight cap) |
| longest broadcast round / follower refresh gap | 1,184 / 1,710 ms | 3,201 / 3,206 ms |
| longest commit-lock wait | 656 ms | 2,590 ms |
| longest persistence-lock wait | 14 ms | 18 ms |
| re-elections / role transitions | 0 / 0 | 2 (terms 3 and 5) / 20 |

The follower refresh gap is the time between two resets of a follower's election timer, which the tool reads from the
`incoming-heartbeats-count` counter. A leader message resets the timer, but so does a vote granted in the follower's
current term, so the gap is a lower bound on the time between leader messages; it is the interval the election timer
itself sees, and the conclusions below hold a fortiori.

The Linux timeline, one row per second: the uncommitted backlog grows by about 700 per second; the longest round is
1.4 s in second 1, 2.2 s in second 4 and 3.2 s in second 10, with commit-lock waits of 880, 1,340 and 2,590 ms in the
same seconds; leaders claim term 3 at 7.2 s and term 5 at 15.0 s, and after each the backlog falls to 0 and grows
again. The persistence lock never waits more than 18 ms in any second, so the device is not the stall: the queue in
front of the lock is. On Windows the cycle is 3.6 times longer, so 120% of the ceiling is fewer writes per second, the
queue grows more slowly (34 to 585 over the run) and the 1.7 s refresh gap stayed just under the election timeouts in
this run; the #118 baseline, with a longer queue, went through 3 terms and 2,133 unknown outcomes.

At 50% and 90% of the ceiling the longest refresh gap was 56 and 124 ms on Linux (146 and 64 ms on Windows): no
starvation below saturation. There is no backpressure in the library: `ReplicateAsync` accepts every proposal, and the
only bound in these runs is the tool's own cap of 4,096 writes in flight, which the Linux run reached.

### Q4: run-to-run spread

The same cells, three rounds each on one host, run round by round (`--repeat 3`), both at revision f48b8657. Windows
with `--diagnostics`; Linux without it, which also shows that the listener does not move the numbers:
the Linux medians are within 4% of the diagnostics run above.

| Cell | Windows acked/s min / median / max (CV) | Linux acked/s min / median / max (CV) |
|---|---|---|
| `wal-append-128B-c1` | 93 / 97 / 98 (2.5%) | 333 / 348 / 387 (7.8%) |
| `wal-append-128B-c16` | 176 / 180 / 194 (5.2%) | 659 / 661 / 679 (1.6%) |
| `wal-append-128B-c16-private` | 170 / 186 / 200 (8.1%) | 702 / 716 / 733 (2.2%) |
| `raft-closed-1v-128B-c16` | 171 / 178 / 185 (3.8%) | – |
| `raft-closed-3v-128B-c1` | 55 / 56 / 57 (1.9%) | 218 / 224 / 225 (1.6%) |
| `raft-closed-3v-128B-c16` | 160 / 161 / 163 (0.9%) | 592 / 593 / 601 (0.8%) |
| `raft-closed-3v-128B-c64` | – | 658 / 661 / 663 (0.3%) |
| `raft-closed-3v-16KiB-c1` | 54 / 56 / 58 (3.8%) | – |
| `raft-open-3v-128B` 50% / 90% | – | CV 0.3% / 0.3% |
| `raft-open-3v-128B-120pct` | – | **317** / 623 / 640 (34.5%) |
| `slow-follower-3v-128B-c16` | – | 524 / 613 / 613 (8.9%) |

Back to back, the closed-loop cells repeat within 1 to 8%. The large differences come from two sources, and the
diagnostics tell them apart:

- **The device or host, in episodes.** In the Windows full run, five cells close together (positions 13 and 18 to 21:
  `wal-append-16KiB-c16-private`, the three `raft-closed-1v-16KiB` cells and `raft-closed-3v-128B-c1`)
  ran persist cycles of 18 to 29 ms instead of 5 to 7 ms. Every phase grew several-fold together, including the
  64-byte intent publish (8.3 ms) and the data-directory flush (2.4 ms instead of 0.19 ms), and no cell re-elected
  (one leader change each, the initial election). The code
  path was the same; the device was slower. That is the run in which `raft-closed-3v-128B-c1` measured 14.4/s, against
  55 to 57/s in every repeat round, and the 1-voter 16 KiB cells 25 to 50/s, against 82 to 191/s for the same sizes on
  the WAL alone. The source of the episode is outside the process (the tool cannot see the device queue or other
  processes); it is consistent with the #118 baseline's 6/s against 58/s for `raft-closed-3v-16KiB-c1`, which had no
  diagnostics to tell. On Linux CI no such episode appeared.
- **Elections, at overload only.** At 120% the Linux round with two re-elections completed 317/s with 7,000 unknown
  outcomes; the two rounds without completed 623 and 640/s with none. Whether a round crosses the 1 to 2 s randomized
  election timeout (H4) decides the result. No closed-loop cell and no open-loop cell below saturation had a
  re-election on either host.
- **The tool** contributes little: the same cell repeats within a few percent when the device is steady. The
  slow-follower cell varies by up to 15% with where the injected pause and the snapshots fall.

**Answer to Q4.** Compare cells only within one run and check the cycle time in `--diagnostics`; repeat a cell with
`--repeat` before reading a difference into it. A cell whose phases are all several times slower than its neighbours'
measured the device, not the code.

### Candidate follow-ups (not filed; ranked by expected gain against risk)

Each would be its own issue with a measured before and after, and must keep every oracle green.

| # | Change | Expected gain | Risk to the durability contract |
|---|---|---|---|
| 1 | Group commit: coalesce concurrent `AppendAsync` calls (and leader proposals from `ReplicateAsync`) into one persist cycle | Up to N times at concurrency N: toward the batch rates (Windows 842/s at b16, Linux 5,905/s), and the end of the FIFO queue that drives H4 | Medium: each caller must be released only after the cycle that covers its entry, so publish-after-durable must hold per batch; the batch boundary needs the same crash tests as `ILogEntryProducer` batches |
| 2 | Lock fairness for compatible waiters: let read (flusher, applier, replication) and commit waiters pass queued appends, or stop holding the append lock across the persist | Removes the heartbeat and replication starvation behind the append queue (H4); bounds the read wait to one cycle | Low for durability (the persistence lock still orders the cycles); a liveness risk if appends starve under continuous reads, so it needs a bound |
| 3 | Fold the commit flush into the next append cycle, or skip it when an append persist already covered the commit index | About 2 times at c1 on a single node or leader (two cycles per write become one) | Low to medium: the commit index must still become durable before anything relies on it after a crash; with no next append, the flusher must still run |
| 4 | Cheaper checkpoint: a smaller slot write than 64 KiB, and an intent that does not need its own create-flush-rename-flush | Up to 5 of the 8 fsyncs and 3.5 of the 5.2 ms of a Windows cycle; 1,000 times less device write per small write | Medium: the intent and the two slots are what make a torn checkpoint recoverable (#24); any change needs the torn-write and recovery tests again |
| 5 | Flush the directories only when a file was created, extended or renamed | 2 of 8 fsyncs; 0.36 ms of a 5.2 ms cycle on Windows, about 0.02 ms on Linux | Low, if every create, extend and rename is covered |
| 6 | Backpressure on proposals, and a heartbeat that does not wait for the append queue | Bounded latency and no elections at overload; `ReplicateAsync` fails fast instead of producing unknown outcomes | Low: rejecting a proposal before it is appended is safe; the heartbeat must still carry a consistent commit index |

1 and 2 attack the queue that the H2 and H4 numbers show; 3 halves the c1 latency; 4 and 5 shorten every cycle.
Leader-side proposal batching (an issue direction) is part of 1. Candidate 2 is done: see
[Lock fairness for compatible waiters](#lock-fairness-for-compatible-waiters-126).

### Lock fairness for compatible waiters (#126)

**Problem.** The WAL `LockManager` granted its locks in strict queue order (H3, H4). A read or a commit queued behind
an append waited for that append and every append ahead of it, each holding the append lock across its persist cycle.
The leader reads a replication round or heartbeat under the read lock, so at overload the rounds stretched past the
election timeout.

**Options considered.**

- **(a) Compatible waiters pass queued ones, with a starvation bound.** Chosen. It changes only the order in which the
  lock manager grants locks; what the locks protect and the persist path are unchanged.
- **(b) Stop holding the append lock across the persist cycle.** Rejected. The append lock is what orders the page
  writes, the checkpoint and the publication of the new last index (publish-after-durable, #24, finding 15). Moving the
  persist cycle out of it would need a separate durable-index publication protocol, a second order between concurrent
  appends, and its own crash tests. It is the same territory as group commit (#125), which rewrites that path anyway.

**Rule.** `QueuedSynchronizer<TContext>` has a new `protected virtual bool CanOvertake(TContext context, TContext
suspended)`. Its base implementation returns `false` and turns overtaking off, so every other synchronizer keeps the
strict queue order. A caller is granted ahead of suspended callers only if `CanAcquire` allows it and `CanOvertake`
returns `true` for every suspended caller ahead of it. This applies both on arrival and when the queue is drained.
`LockManager` returns `true` only when holding `context` cannot make `CanAcquire(suspended)` false, so passing never
delays the passed caller:

| Arriving \ suspended | Read | ReadBarrier | Append | Commit | Overwrite | Flush |
|---|---|---|---|---|---|---|
| Read | FIFO | no: a reader blocks the barrier | yes | yes | no | yes |
| ReadBarrier | yes | FIFO | yes | yes | no | yes |
| Append | yes | yes | FIFO | yes | no | yes |
| Commit | yes | yes | yes | FIFO | no | yes |
| Overwrite | no | no | no | no | FIFO | no |
| Flush (the flusher) | no | no | no | no | no | FIFO |

How each pair was checked: holding Read, Flush or ReadBarrier changes only the reader count, which blocks ReadBarrier
and Overwrite. Holding Append blocks only Append and Overwrite. Holding Commit blocks only Commit and Overwrite.
Overwrite is excluded both ways: nothing passes a queued upgrade, which `AcquirePriorityAsync` places at the head of
the queue, so the deadlock fix for upgrades (#37, finding 10) is unchanged. An upgrade passes nobody.

**Flusher exception.** With the rule alone, the flusher's read lock passed queued appends. It then took the
persistence lock between two append cycles, so a node ran one flush cycle per append cycle instead of one per batch.
On Linux this cut WAL and 1-voter throughput by 36 to 42%. The flusher now takes a separate `Flush` lock. It is
compatible with the same locks as Read and anyone may pass it, but it passes nobody. Its pass therefore comes after
the appends that were queued when it arrived, as before #126. Appends that arrive later queue behind it, because they
cannot pass the earlier appends. The flusher's wait is still reported as `lock-wait-duration{lock=read}` with cause
`flush`.

**Safety.** The lock compatibility matrix (`CanAcquire`) is unchanged, so no two incompatible holders can coexist,
and the persist path, the persistence lock and publish-after-durable are untouched. Only the order of grants changes.
Callers of one kind keep their relative order: appends append in arrival order, and commits apply in arrival order.

**Liveness.**

- A passed caller is never made unacquirable by the caller that passed it, so it waits for no more than before.
  Overtaking can only shorten waits.
- Appends cannot be starved by continuous reads, because a reader never blocks an append. Readers can delay only a
  ReadBarrier or Overwrite, and nobody passes those.
- The upgrade cannot deadlock. It waits for the readers that hold the lock, every new reader queues behind it, and the
  holders finish without needing the append lock.
- The drain remembers up to 8 distinct contexts of the callers it has passed: equal contexts are kept once, so a run
  of blocked appends takes one entry. If a ninth distinct context is blocked, the drain falls back to the strict
  order, and the waiters behind it wait no longer than they would in FIFO. The WAL has six lock types, so the
  WAL never reaches this limit and a drain can pass any number of blocked callers. The rule above, not this limit,
  is what keeps appends from starving. `CanOvertake` must therefore give equal contexts the same answer, which an
  override over an enum does by construction.
- A cancelled suspended caller drains the queue, so the waiters it was blocking are granted.

**Tests** (each is deterministic: it fills the queue with blocked appends and checks which waiters are granted, with
no timing):

| Test | Checks |
|---|---|
| `WriteAheadLogLockManagerTests.CompatibleWaitersDoNotQueueBehindPendingAppends` | Red before #126: read, commit and apply waiters behind N queued appends are granted at once |
| `WriteAheadLogTests.ReadAndCommitDoNotWaitForQueuedAppends` | Red before #126: on a real WAL, a read and a commit complete while the appends are blocked in their persist cycle |
| `AppendsAreNotStarvedByContinuousReaders` | The append starvation bound |
| `ReadersAndCommittersDoNotPassQueuedUpgrade`, `UpgradeAfterReaderPassedQueuedAppends`, `UpgradeWaitsForFlush` | The upgrade path and overwrite |
| `QueuedReadersAndCommittersPassBlockedAppendAfterOverwrite`, `ReadersDoNotPassQueuedReadBarrier`, `CommittersStayInOrder`, `CanceledBlockedWaiterReleasesWaitersBehindIt`, `FlushKeepsQueueOrderBehindPendingAppends` | The remaining pairs |
| `QueuedSynchronizerTests.CallersKeepQueueOrderByDefault`, `CallerPassesSuspendedCallersThatItCannotDelay`, `DrainStopsWhenTooManyCallersArePassed` | The base class hook |

**Measured** with `--profile full --diagnostics` on the #123 cells. The Linux runs are CI `workflow_dispatch` on
ubuntu-24.04 with shared memory (before: run 37476399824; reads and commits only: 37479182899; final: 37484876122).
The open-loop cells offer a percentage of each run's own closed-loop ceiling, so the absolute rates differ between
columns.

| Linux | before | final |
|---|---:|---:|
| `wal-append-128B` c16 / c64, ack/s | 610 / 654 | 583 / 666 |
| `raft-closed-1v-128B` c16 / c64, ack/s | 652 / 642 | 659 / 687 |
| `raft-closed-1v-128B-c64` commit-lock wait p99 | 112.6 ms | 0 |
| `raft-closed-3v-128B` c16 / c64, ack/s | 591 / 668 | 476 / 489 |
| `raft-closed-3v-128B-c64` leader read-lock wait p99 / longest round / longest refresh gap | 85.8 / 123 / 124 ms | 0 / 20 / 21 ms |
| flusher read-lock wait p50, `wal-append-128B` c16 / c64 | 11.1 / 84.2 ms | 24.3 / 92.4 ms |
| `raft-open-3v-128B` 50% / 90%: longest round | 23 / 101 ms | 15 / 61 ms |
| `raft-open-3v-128B-120pct` offered / completed per second | 801 / 653 | 587 / 461 |
| `raft-open-3v-128B-120pct` leader read-lock wait p99 / commit-lock wait p99 | 1,178 / 1,172 ms | 0.6 / 0.1 ms |
| `raft-open-3v-128B-120pct` longest round / longest refresh gap | 2,570 / 1,589 ms | 63 / 63 ms |
| `raft-open-3v-128B-120pct` role transitions / unknown outcomes | 14 / 0 | 0 / 0 |

The run with only reads and commits passing (no flusher exception) measured 351 / 399 ack/s for the WAL cells,
404 / 410 for the 1-voter cells and 372 / 382 for the 3-voter cells, each with one flush cycle per append. It also
measured 0 role transitions and a 54 ms longest round at 120%.

| Windows, local, back to back | before | final |
|---|---:|---:|
| `wal-append-128B` c16 / c64, ack/s | 155 / 139 | 177 / 194 |
| `raft-closed-1v-128B` c16 / c64, ack/s | 47 / 47 | 51 / 27 |
| `raft-closed-3v-128B` c16 / c64, ack/s | 167 / 198 | 95 / 98 |
| `raft-closed-3v-128B-c64` leader read-lock wait p99 / longest round / longest refresh gap | 303 / 334 / 333 ms | 0 / 83 / 91 ms |
| `raft-open-3v-128B-120pct` offered / completed per second | 238 / 0.1 | 118 / 98 |
| `raft-open-3v-128B-120pct` leader read-lock wait p99 / commit-lock wait p99 | 1,839 / 1,214 ms | 0 / 0 ms |
| `raft-open-3v-128B-120pct` longest round / longest refresh gap | 1,617 / 1,621 ms | 26 / 28 ms |
| `raft-open-3v-128B-120pct` leaders / role transitions / unknown outcomes | 7 / 27 / 5,127 | 1 / 0 / 0 |

The Windows host was in a slow-device episode for part of these runs (Q4). The `raft-closed-1v-128B` cells ran 20 ms
(before) and 37 ms (final) persist cycles, against about 5 ms in the WAL cells. Both ran one cycle per write, so those
two cells measured the device. Linux, where this cell went from 642 to 687 per second, is the reference for them. An
earlier Windows run before the change, in a steady period, also re-elected at 120%: 5 leaders, 25 role transitions,
3,265 unknown outcomes and a 3.5 s longest round.

**3-voter closed-loop throughput is lower**, by 20 to 27% on Linux and about half on Windows. Before #126 the leader's replication read waited behind the whole
append queue. That batched replication as a side effect: followers wrote 7.6 entries per persist cycle at c16 and
31.5 at c64, so only the leader paid one cycle per write. Now a round goes out as soon as the previous one returns. It
carries 1 to 1.8 entries, and one follower persist cycle per write sets the ceiling. At 120% the absolute rate the
final run offered (587/s) is about what the baseline completed at 90% (602/s). The difference is what happens beyond
the ceiling: the baseline's rounds then stretch with the queue until followers start elections, while the final
rounds stay at one cycle. Group commit (#125) appends in batches on the leader and should restore follower batching.
Throughput beyond one cycle per write belongs there, not in the lock order.

**Risks.**

- The 3-voter closed-loop ceiling above.
- The arrival check scans the queue, which costs O(queue length) per acquisition that finds waiters. The queue is
  bounded by the number of concurrent callers.
- `CanOvertake` is a new protected API on a public type. Its base implementation doubles as the switch that keeps the
  old order, and the docs tell overrides not to call it.
- Group commit (#125) changes the append path. The two meet only in `WriteAheadLog.LockManagement.cs` and
  `WriteAheadLog.Flusher.cs`.

Filed as #125 (1, done: see [WAL group commit (#125)](#wal-group-commit-125)), #126 (2) and #127 (the flaky tests).

## WAL group commit (#125)

**Change.** Concurrent single-entry `WriteAheadLog.AppendAsync` calls whose payload is in memory or can be
formatted into a buffer (`BinaryLogEntry`, `IBufferedLogEntry`, entries that supply their own buffer, and the
leader's proposals from `RaftCluster.ReplicateAsync` through `AppendInCurrentTermAsync`) now share one persist cycle.
Each call enqueues a request and wakes one committer (`WriteAheadLog.GroupCommit.cs`). The committer takes the append
lock and the persistence lock once, drains every request queued at that moment (no bound, as for an
`ILogEntryProducer` batch), writes the entries in queue order, runs one `PersistAppendAsync` and only then completes
the requests. Streamed entries, snapshots, `ILogEntryProducer` batches, overwrites and term-change appends are
unchanged: they still take the locks themselves and run their own cycle. So do configuration entries. The cluster
activates a configuration only after its append completes (`RaftCluster.AppendConfigurationAsync`). A grouped
append resumes its caller through the thread pool, and during that delay a replication round could take the new
`LastEntryIndex` with the old membership and commit the configuration under the old quorum.
`LogDerivedConfigurationTests.LeaderStepsDownOnceItsRemovalIsCommitted` caught this in CI (2 of the 4 remaining
voters held the removal). The direct path resumes the caller inline, as before #125. The window that remains is
the same as before #125, and it is narrow: a timer heartbeat between publication and activation.

A cleanup failure is isolated as well. Each request is settled even if the allocator's owner throws when its
buffer is released, so a release failure cannot strand the rest of the batch (review of #132). Buffers are
released only after the committer has released the append and persistence locks, as on the single-entry path
before #125. That includes the buffers of requests rejected by their own checks, such as a stale-term proposal.
A formatted entry's buffer, whether it comes from a custom `Options.Allocator` or from an owner the entry supplies
itself, is released on the thread pool rather than on the committer. The WAL cannot tell a pool owner from user code,
so an owner that blocks on, or re-enters, the WAL from `Dispose` (even with a grouped append it waits for) cannot
deadlock the committer. The release is one work item per formatted entry, with no extra allocation (the request is
the work item). A request canceled while queued releases its buffer inline, on the canceling thread.

**Contract, per caller.**

- An append completes only after the cycle that covers its entry has flushed the pages, the directories and the
  checkpoint; `LastEntryIndex` is still published by that cycle, after it is durable (#24, finding 15).
- If the cycle fails, every request it covers fails with the same exception and the WAL is faulted, as a failed
  single append faulted it before; none is acknowledged.
- The current-term guard (`requireCurrentTerm`) and the fault check run per request when the batch is staged: a
  stale proposal fails alone and the rest of the batch is written.
- Cancellation is observed while a request is queued: a request canceled then is removed and never written. Once
  the committer stages it, the request completes with its batch, so cancellation does not abandon a written entry.
  Before, cancellation was observed until the mutation started; the window is the same in effect (queue versus
  lock wait).
- Disposal fails the requests still queued with `ObjectDisposedException`.

No option was added and no default changed. An enqueue that finds the committer idle wakes it inline (no thread hop
when the locks are free), and the requests complete asynchronously, so a caller's continuation never runs on the
committer.

**Red first.** `WriteAheadLogGroupCommitTests.ConcurrentAppendsShareOnePersistCycle` counts `persist-phase-duration`
measurements with `cause=append` through a `MeterListener` filtered on the WAL's `MeasurementTags`. It holds the
first append's cycle open and queues 7 more behind it. On `fork` the 8 appends cost 8 cycles (the test failed with
8); with the change they cost 2: the first and one shared by the other 7.
`InProcess.GroupCommitProposalTests.ConcurrentProposalsShareLeaderPersistCycles` shows the same through
`ReplicateAsync` on a 3-voter in-process cluster (red on `fork`).

**Coverage.** `WriteAheadLogGroupCommitTests`: the shared cycle; a request canceled while queued is not written
and the rest are; a request canceled after its cycle started completes; a failed persist faults every covered
request and none is published; a stale-term proposal fails alone; an overwrite queued behind a group cycle does not
deadlock (finding 10, the upgrade path) and appends queued behind an overwrite share one cycle after it; every
buffered entry kind joins the cycle; disposal fails queued requests. `WriteAheadLogDurabilityTests.
GroupCommittedAppendsSurviveProcessTermination` kills the worker process right after a group is acknowledged
(private and shared memory) and checks that the reopened WAL holds the whole group and appends after it.

### Finding: an empty heartbeat round waited for the slowest member

The slow-follower cell (3 voters, one follower behind a relay that pauses its traffic for 5 s) regressed with group
commit alone: proposals stalled for the whole pause instead of committing on the two healthy voters. The cause predates this change but was hidden by the
one-cycle-per-ack rate. The commit-first rule in `ReplicationProcess` counts a member toward commitment only when it
reports `Replicated(index)`; an accepted empty heartbeat reported `Touched`. With group commit the leader appends a
batch, a round replicates it to the fast follower, and the next round is often empty for that follower. That
answer was `Touched`, so the round waited for the paused member to complete the commit majority.

`ReplicationProcess.ConvertToResult` now reports `Replicated(precedingIndex)` for an accepted empty heartbeat whose
preceding entry has the leader's term. By the Log Matching property that acceptance proves the member stores the
leader's log up to that entry, so it is safe to count (Raft §5.4.2: only entries of the current term are committed
by counting replicas). Rounds that carry entries or a snapshot, and heartbeats whose preceding entry has an older
term, are unchanged. `ReplicationProcessHeartbeatTests` covers both sides (red on the old code). After the fix the
slow-follower cell completed in 4 of 4 runs.

Side effect: an empty round no longer waits for an unresponsive member, so the leader marks it unresponsive at the
start of the next round (`LeaderState.StartReplication` checks `IsAvailable`), as rounds with entries already did.
`LeaderReadFailureAttributionTests.PeerTransportFailureIsStillReportedAsUnresponsive` now forces a round on each
step of its wait.

A shorter stall of about 3 s remains in that cell: a linearizable read barrier queued behind appends on the
strict-FIFO `LockManager`. It appears with the old binaries too and belongs to #126.

### Before and after

Same tool (`--profile full --diagnostics --repeat 3`), median acked/s of 3 rounds; "cyc/ack" is the leader's append
cycles per acknowledged write and "ent/cyc" the entries per cycle.

**Linux** (CI `workflow_dispatch`, before 37480280376 on `fork`, after 37489433429):

| Cell | Before | After |
|---|---:|---:|
| wal-append c1 | 374 | 430 |
| wal-append c16 | 647 (p50 22.9 ms) | 6,685 (p50 2.3 ms; cyc/ack 0.063, ent/cyc 16) |
| wal-append c64 | 662 (p50 94.5 ms) | 24,594 (p50 2.6 ms; ent/cyc 63.8) |
| wal-append c16, private memory | 696 | 6,237 |
| wal-batch b16 / b256 (path unchanged) | 5,911 / 72,428 | 6,702 / 79,779 |
| raft-closed 1 voter c1 / c16 / c64 | 377 / 637 / 662 | 430 / 6,609 / 23,622 |
| raft-closed 3 voters c1 / c16 / c64 | 222 / 585 / 637 | 249 / 2,757 / 10,421 (ent/cyc 8.7 at c16, 35.4 at c64; p50 5.9 ms) |

3-voter open loop on Linux. Before: 120% offered about 764/s and completed 611, 475 and 86/s across the rounds,
with up to 7,135 unknown outcomes, 2 to 3 leader changes and rounds up to 3.9 s. After: 50%, 90% and 120% offered
5,210, 9,377 and 12,501/s; every level kept up with 0 unknown outcomes, 1 leader, rounds of at most 37 ms and a p99
of about 36 ms.

**Windows**, interleaved old, new, old, new on one host and one device (`--cells wal-append-128B,wal-batch-128B,
raft-closed-3v-128B`), one round each:

| Cell | Old (2 runs) | New (2 runs) |
|---|---:|---:|
| wal-append c1 | 100 / 101 | 102 / 103 |
| wal-append c16 | 194 / 181 (p50 77 / 73 ms) | 1,602 / 1,610 (p50 9.7 ms; ent/cyc 16) |
| wal-append c64 | 195 / 151 (p50 314 / 305 ms) | 5,619 / 6,183 (p50 10–11 ms; ent/cyc 63) |
| raft-closed 3 voters c1 | 57 / 60 | 22 / 60 |
| raft-closed 3 voters c16 | 161 / 169 (p50 96 / 90 ms) | 731 / 593 (p50 22 / 23 ms; ent/cyc 15) |
| raft-closed 3 voters c64 | 192 / 192 (p50 330 / 328 ms) | 2,699 / 2,635 (p50 23–24 ms; ent/cyc 61–64) |

The cycle itself did not change (4.9 to 5.6 ms per cycle on both binaries when the device is steady). The new c1
outlier (22/s, 13 ms per cycle) and the old run 2's slow private-memory and batch cells (17 to 25 ms per cycle) are
device episodes as described in the #123 Q4 answer. The explicit-batch path, run alone three times interleaved, is
the same on both binaries: b256 at 19,407 to 21,124/s old and 20,783 to 21,423/s new.

The full Windows after-run (`--repeat 3`) agrees: wal-append c16 and c64 at 1,558 and 5,910/s (ent/cyc 16 and
63.5), raft-closed 3 voters c16 and c64 at 682 and 2,590/s. Its open loop offered about 3,000/s at 120% and kept up
with 0 unknown outcomes and rounds of at most 94 ms; the before-run on the same host had up to 5,705 unknown
outcomes and 5 leader changes at 120% (the before full run was degraded by device episodes; use the A/B above for
ratios).

**Open-loop caveat.** The tool sets the open-loop rate as a fraction of the busiest closed-loop cell with the same
voters (c64). With group commit that cell is 15 to 40 times faster and an open-loop backlog of up to 4,096 requests
forms larger batches than c64, so "120%" no longer saturates the leader. The H4 overload behaviour (elections and
unknown outcomes at overload) is therefore not exercised at the same absolute rate; it would need a higher fraction
or a proposal rate that outruns the larger batches. Backpressure remains candidate 6.

**Oracles.** The smoke profile exits 0; `--inject drop-applied`, `reorder-applied` and `non-durable-followers` each
exit 3 with the same oracle as before (apply order, committed-prefix agreement and durability).

**Risks.**

- Batch fairness: the batch has no bound, so one cycle can write a large backlog and the requests queued behind it
  wait for that cycle. This is the same as one large `ILogEntryProducer` batch.
- One failure faults the whole batch and every covered caller observes the same exception instance.
- The first enqueuing caller runs the committer up to its first await (lock acquisition) on its own thread.
- The empty-heartbeat accounting change moves unresponsive detection for empty rounds one round later.
- #126 (lock fairness) changes `WriteAheadLog.LockManagement.cs`; the committer only uses the existing
  `AcquireAppendLockAsync` and persistence lock, so a rebase should be mechanical.

## Protocol input budgets (#22)

**Question.** Can a peer that sends a malformed, inconsistent or stalled request make a node allocate memory
beyond what it received, hold `transitionLock` or the WAL append lock without a bound, or accept an ambiguous
or corrupt payload? The budgets come from existing settings, not new caps: the node's `RequestTimeout`
(the TCP server receive timeout), the declared `Content-Length` when it is known, and the bytes actually
received. Requests of unknown length (chunked) remain valid. Allocation is measured with
`GC.GetTotalAllocatedBytes` against a 32 MiB budget in the non-parallel `AllocationBudget` test collection.
Peers are assumed non-Byzantine (see the security assessment), but R1, R2 and R3 are reachable by a correct
leader: its TCP client closes the connection gracefully on `RequestTimeout`, on step-down and on any error.

**Evidence.** Baseline `fork` @ `8045153e3b7a14b53880402974b83b98069f9470`, Debug, one class at a time:
`dotnet run --project src\DotNext.Tests\DotNext.Tests.csproj -- --filter-class '<Class>' --progress off`.
The guard tests below pass on the baseline. The RED tests were run on the same baseline and were not committed with
the investigation; each follow-up added its own red test and fixed the finding. The Result column keeps the baseline
outcome and states the outcome after the fix.

| Candidate | Contract | Test | Result | Follow-up |
|---|---|---|---|---|
| C1 TCP stall | A stalled AppendEntries or InstallSnapshot body releases `transitionLock` within `RequestTimeout`, and the log stays usable | `ProtocolInputBudgetTcpTests.Stalled*BodyReleasesTransitionLock` | GREEN: released after about 1.0-1.1 s with `RequestTimeout` = 1 s | - |
| C1 HTTP stall | Same, for the HTTP endpoint, with and without Kestrel's `MinRequestBodyDataRate` | `ProtocolInputBudgetHttpTests.StalledBodyReleasesTransitionLockWithinRequestTimeout` | Was **RED (R3)**: no server-side deadline. Without `MinRequestBodyDataRate` the lock was held for more than 15.3 s; with the Kestrel default it was released after about 5.75 s, and then R2 applies. GREEN after the fix: AppendEntries, InstallSnapshot and Synchronize are bounded by the node's `RequestTimeout`; the lock is released after about 1.0 s with `RequestTimeout` = 1 s, the connection is aborted, the partial write rolls back and the log stays usable | R3 (#91), fixed by #100 |
| C1 disconnect (TCP, HTTP) | A peer that disconnects or times out mid-body costs that request only; the log stays usable | `ProtocolInputBudgetTcpTests.PeerDisconnectMidBodyLeavesLogUsable` (reset), `ProtocolInputBudgetHttpTests.PeerDisconnectMidBodyLeavesLogUsable`, `MinRequestBodyDataRateMidBodyLeavesLogUsable` | Was **RED (R2)**: a TCP reset, an HTTP FIN or reset, and a Kestrel minimum data rate failure all reached `OnBackgroundTaskFailure`; every later append and heartbeat failed until the WAL was reopened. GREEN after the fix: the transport failure cancels the request and the partial write rolls back | R2 (#90), fixed by #98 |
| C1 truncation (TCP) | An entry or snapshot whose received payload is shorter than its declared length is never persisted | `ProtocolInputBudgetTcpTests.PeerDisconnectMidBodyLeavesLogUsable` (FIN), `RetransmissionAfterGracefulCloseMidEntryIsStoredInFull`, `DeclaredEntryLengthDoesNotDriveAllocation`, `SnapshotShorterThanDeclaredLengthIsNotInstalled` | Was **RED (R1, critical)**: a FIN mid-body persisted entry 1 with 16 of 1024 bytes, and a complete retransmission at the same index and term did not repair it; an InstallSnapshot installed index 10 from 16 of 1024 bytes; a final frame shorter than the entry or snapshot length was persisted without any disconnect. HTTP rejects these cases. GREEN after the fix: EOF inside a frame and a length mismatch cancel the request, the partial write rolls back, and the retransmission is stored in full | R1 (#97), fixed by #99 |
| C2a config length | A negative `X-Raft-Config-Length`, or one larger than the known `Content-Length`, is rejected; nothing is installed and the node stays available. Chunked requests are accepted | `ProtocolInputBudgetHttpTests.InconsistentConfigurationLengthIsRejected`, `ChunkedInstallSnapshotIsAccepted` | GREEN: -1, `int.MinValue` and 1024 (with 16 bytes sent) are rejected with 500 | - |
| C2b config member count | An invalid configuration payload (negative member count, or more members than the payload holds) is rejected without replacing the applied or stored configuration | `ConfigurationPayloadBudgetTests` (in-memory and persistent storage, including reopen), `ProtocolInputBudgetHttpTests.MalformedSnapshotConfigurationIsNotApplied` | Was **RED (R4)**: a count of -1 decoded as an empty member set (HTTP 200, members 2 to 0, version 0 to 5; storage `Save` returned true); a count of 1000 or `int.MaxValue` was persisted before decoding, so every later load threw `EndOfStreamException`, including after reopen (HTTP 500). GREEN after the fix: the request is rejected with `RaftProtocolException`, nothing is persisted or applied, and the node serves the next request | R4 (#92), fixed by #101 |
| C3a staged config | Memory reserved for the staged snapshot configuration is proportional to the bytes received | `ProtocolInputBudgetHttpTests.DeclaredConfigurationLengthDoesNotDriveAllocation`, `ProtocolInputBudgetTcpTests.DeclaredConfigurationFrameLengthDoesNotDriveAllocation` | Was **RED (R5)** on HTTP: 128 MiB declared with `Content-Length` 8 allocated 134 MB; 256 MiB declared over chunked allocated 268 MB. GREEN after the fix: 285 KB and 170 KB, both requests still rejected. TCP was never affected: the configuration is a sequence of frames with no declared total length, and a frame announcing 128 MiB with 8 bytes delivered allocated 90 KB. A valid chunked InstallSnapshot with a real configuration is still accepted (`ChunkedInstallSnapshotWithConfigurationIsAccepted`) | R5 (#94), fixed by #102 |
| C3b entry count | A declared entry count beyond the entries delivered drives no allocation, and the follower commits or acknowledges only what it received | `ProtocolInputBudgetTcpTests.DeclaredEntryCountDoesNotDriveAllocation`, `ProtocolInputBudgetHttpTests.EntryCountBeyondDeliveredEntriesIsNotAcknowledged` | GREEN for the WAL (rejected, nothing committed). Was **RED (R6)** for `ConsensusOnlyState`: 2^25 declared over TCP allocated 268 MB, and HTTP multipart with fewer sections than declared committed a stale tail and a phantom entry (200, `X-Raft-Last-Index` 4). GREEN after the fix: TCP allocates under 32 MiB and closes the connection; HTTP multipart is rejected with 500 and nothing is committed (both parametrized with `useWal: false`) | R6 (#96), fixed by #103 |
| C3c entry length | A declared entry length beyond the delivered payload drives no allocation | `ProtocolInputBudgetTcpTests.DeclaredEntryLengthDoesNotDriveAllocation`, `ProtocolInputBudgetHttpTests.DeclaredEntryLengthDoesNotDriveAllocation` | GREEN: under 1 MB for 2^30 and `long.MaxValue`. Persistence of the short entry over TCP is R1 | - |
| C3d metadata count | A declared metadata pair count drives no allocation before the pairs arrive | `MetadataPayloadBudgetTests.DeclaredPairCountDoesNotDriveAllocation`, `MetadataPayloadBudgetTests.TruncatedPairsAreRejected`, `MetadataPayloadBudgetTests.ValidMetadataRoundTrips` | Was **RED (R7)**: 2^23 declared pairs allocated 235 MB then threw `EndOfStreamException`; a negative count threw `ArgumentOutOfRangeException`. GREEN after the fix (#104): under 32 MiB for 2^23, `int.MaxValue` and truncated payloads; a negative count or truncated payload is rejected with `RaftProtocolException`; valid metadata round-trips (TCP client reading a peer's metadata reply) | R7 (#93), fixed by #104 |
| C4 duplicate headers | A repeated singleton `X-Raft-*` header (or multipart section header) is rejected without a state change, even with identical values | `ProtocolInputBudgetHttpTests.DuplicateSingletonHeaderIsRejected` (request headers), `DuplicateEntrySectionHeaderIsRejected` (multipart section headers), `DuplicateResponseHeaderIsRejected` (response headers the leader parses); controls `SingleSingletonHeaderIsAccepted`, `SingleEntrySectionHeaderIsAccepted`, `SingleResponseHeaderIsAccepted` | Was **RED (R8)**: 12 of 12 request cases were accepted with 200 and changed state; the first parseable value won (`HttpMessage.TryParseHeader`). GREEN after the fix: a repeated singleton header, even with identical values or an unparseable first value, is rejected with `RaftProtocolException` (500) for request, multipart section and response headers, and nothing changes; a single header and an absent header behave as before | R8 (#95), fixed by #105 |

**Fixes (one issue and pull request each).**

- R1 (#97): `ProtocolStream` throws `EndOfStreamException` at transport EOF inside a frame, and received entries and
  snapshots compare the copied bytes with the declared length. No wire change.
- R2 (#90): the server readers turn transport read failures into cancellation of the request, so the rollback of #53 and #73 applies.
  Storage I/O errors stay fail-closed.
- R3 (#91): the HTTP handler token includes the node's `RequestTimeout`, as on TCP. Depends on R2. Fixed by #100 for
  AppendEntries, InstallSnapshot and Synchronize; nodes of one cluster should use the same `RequestTimeout`.
- R4 (#92): `ClusterConfigurationStorage` rejects a negative count and decodes the payload before persisting it, and a staged snapshot configuration is decoded when it is received. Fixed by #101; the member count is not capped.
- R5 (#94): the staged configuration buffer grows as data arrives and the received length is checked against the declared one; a declared length above a known `Content-Length` is rejected. Fixed by #102; the configuration size is not capped.
- R6 (#96): the multipart reader rejects a section count that differs from `X-Raft-Entries-Count`; `ConsensusOnlyState`
  allocates for the entries enumerated, not the declared count, and rejects a shortfall with `RaftProtocolException`.
  Fixed by #103; chunked bodies are unaffected and there is no new limit.
- R7 (#93): `MetadataTransferObject` reads without a capacity hint taken from the count, rejects a negative count and a payload shorter than its count with `RaftProtocolException`. Fixed by #104; there is no wire change.
- R8 (#95): both `TryParseHeader` overloads require exactly one value and throw `RaftProtocolException` otherwise. Fixed by #105; every
  header parsed by `HttpMessage` is a scalar, so none is legitimately multi-valued, and there is no wire change.

**Review hardening of the fixes (#89).** Pull request review found gaps in the R1–R3 fixes, each covered by a test:

- The remapped cancellation token hid the receive timeout from the TCP server log; the handlers now rethrow the cancellation of the caller's token (`StalledBodyIsReportedAsRequestTimeout`).
- The TCP zero-copy fast path accepted a final frame that was larger than, or announced more than, the declared entry length;
  it is now taken only for a final frame of exactly the declared length (`BufferedFinalFrameThatDoesNotMatchEntryLengthIsNotPersisted`).
- HTTP reads of the entry framing (21-byte metadata, skipping, multipart section headers) passed no token, so with Kestrel's
  `MinRequestBodyDataRate` disabled a stall held the locks past `RequestTimeout`; `PayloadReader` now bounds them by the request token (`StalledEntryFramingReleasesTransitionLockWithinRequestTimeout`).
- A short octet-stream entry in a complete HTTP body, and a multipart section count or section header error, a malformed
  multipart header line, or a body that ends before the closing boundary after the first entry, faulted the WAL instead of rolling back; they are now reported as cancellation of the request, like a transport failure
  (`DeclaredEntryLengthDoesNotDriveAllocation`, `MultipartRequestFailingAfterFirstEntryLeavesLogUsable`). Entries received completely before the failure may stay in the log.

**Informational.** `ConsensusOnlyState` accepts an HTTP octet-stream entry whose payload is shorter than its
declared length; it discards payloads by design, so nothing is persisted.

## Failure signals and operator actions (#26)

**Question.** Can an operator tell expected leadership loss, intentional standby or removal, failure-induced standby,
terminal storage failure, integrity failures at open, and malformed peer input apart, and know what to do in each case?
This section inventories the signals after the fixes listed above. Paths are relative to
`src\cluster\DotNext.Net.Cluster\Net\Cluster\Consensus\Raft\`, line numbers refer to `fork` @ `dc756aeae`, and event
ids are `LogMessages` ids (offset 74000).

| Case | Signals | Operator action |
|---|---|---|
| Expected leadership loss | Debug 74000 `DowngradingToFollowerState` (`RaftCluster.cs:650`); `LeadershipToken` cancelled; `ReplicateAsync` throws `NotLeaderException`; unavailable-member processing abandoned at Debug 74047 (#51, `RaftCluster.cs:1566`) | None; clients follow the new leader |
| Intentional standby or removal | `EnableStandbyModeAsync`/`RevertToNormalModeAsync` return values (`RaftCluster.cs:475,517`); a removed leader steps down to resumable standby (6.8.1) | None |
| Failure-induced standby | Critical 74032 `TransitionToLeaderStateFailed`, then resumable standby (`RaftCluster.cs:1534-1535`) | Fix the cause, then `RevertToNormalModeAsync` or restart |
| Failure-induced zombie | Critical 74030/74031 `TransitionTo{Follower,Candidate}StateFailed` (`RaftCluster.cs:1381,1474`); `Readiness`, election and leadership waits fault with the cause (`RaftCluster.cs:1301-1311`) | Fix the cause, then restart |
| Worker failure, node keeps running | Error 74035 `LeaderStateExitedWithError` (#8, `LeaderState.cs:112`) or Error 74048 `VotingFailed` (`CandidateState.cs:47`), then follower | Investigate the logged exception; repeated failures escalate to 74030/74031 |
| Leader cannot read its own log for a peer | Error 74049 `LocalLogReadFailed` with the peer endpoint and exception, once per replication round for that peer (#115, `ReplicationUtils\ReplicationProcess.cs`). The peer is not reported as unresponsive. If a majority cannot be replicated, the leader steps down on quorum loss (Debug 74000) | Investigate the leader's storage. The leader is not stepped down automatically while it keeps a majority: fix storage, then `ResignAsync` or restart the leader |
| Terminal WAL failure | The WAL logs nothing; it keeps its first failure (`StateMachine\WriteAheadLog.Error.cs:14-22`). Read, append, commit, apply waits and flush throw `WriteAheadLog.InternalException` (an `IntegrityException`) with that failure as the inner exception (#13). Raft surfaces it through the transition or worker events above | Fix storage, then reopen the WAL (restart). `Readiness` reflects it only once the node is a zombie |
| Integrity failure at open | The WAL or configuration storage constructor throws `IntegrityException` (#83, #106), or replay throws `HashMismatchException`/`MissingPageException`; the host fails to start | Restore from backup, or remove the member and re-add it with an empty WAL directory |
| Malformed peer input | `RaftProtocolException`: TCP Error 74028 `FailedToProcessRequest` with the remote endpoint (`NetworkTransport\ConnectionOriented\Tcp\TcpServer.cs:149`), HTTP 500. Transport payload failures become request cancellation (#90) | None on the node; it stays available. Distinguish 74028 by exception type |

Per worker: heartbeat and replication are supervised (#8, 74035). A per-peer replication failure before the request to
the peer is logged as Error 74049 (see G2); a failure of the request itself is still logged as EventId 0 and counts
against the peer's failure detector. The WAL applier, flusher and cleanup report through `OnBackgroundTaskFailure` only. Background
snapshots report through `SimpleStateMachine.OnSnapshotFailed` (#75). The unavailable-member detector logs 74037,
74036 or 74047. Follower election timeouts go through `MoveToCandidateState` (74031 on failure). Standby and
readiness are described above.

**G1, candidate voting (fixed).** The voting task started by `CandidateState` was not supervised. An exception from the
last-term read, a voter, or the leader no-op append ended the task silently, and the node stayed a candidate with no
election in progress and nothing logged above Debug. `CandidateVotingFailureTests` reproduces it on `c4cb19ad7`: one
injected `IOException` left the node in `CandidateState` at term 1 after 10 election timeouts. The fix logs Error
74048 `VotingFailed` with the term and exception and returns to follower with a randomized timeout, so elections
resume. A failure during disposal is still reported at Debug 74034. A persistent storage failure is not retried
indefinitely here: the next transition fails and the node becomes a zombie (74031).

**G2, leader local read failure (fixed, #115).** When the leader could not read its own log for one peer's range,
`ReplicationUtils\ReplicationProcess.cs` logged EventId 0 without the peer endpoint and queried the peer's failure
detector without a heartbeat. The healthy peer was then reported as Warning 74037 `UnresponsiveMemberDetected` and the
default implementation removed it. Red evidence: `957bb6c1d` on branch `dh/issue-26-leader-read-attribution-red`,
`LeaderReadFailureAttributionTests` (three EventId 0 entries, then 74037 for `node-2`).

The replication process now records, per round, when it starts the request to the member: immediately before
`AppendEntriesAsync`, and before `InstallSnapshotAsync` after the configuration is loaded. A failure before that point
(the preceding-term read, `IAuditTrail.ReadAsync`, or the configuration load) is a local failure: it is logged as
Error 74049 `LocalLogReadFailed` with the peer endpoint and the exception, the round reports the peer as unavailable
for quorum, and the failure detector is neither fed nor queried. A failure after that point, including any transport
exception, is handled as before (EventId 0, or `MemberUnavailableException`, then the failure detector and 74037).
Cancellation is unchanged. The event replaces the EventId 0 entry one for one, so it is emitted once per round per
peer, never per entry. If every read fails, the leader steps down on quorum loss and no peer is blamed.

Residual gap: the WAL reads entry payloads lazily, while the transport serializes the request. A payload read failure
at that stage is indistinguishable from a transport failure without changing the transports, and is still attributed
to the peer. Eager failures, such as a terminal WAL failure (`WriteAheadLog.InternalException`), disposal, or a custom
`IPersistentState` that fails in `ReadAsync`, are covered.

Design choice: attribution only. A leader that can still replicate to a majority but not to one lagging peer stays
leader. Stepping down instead (the #8 and #116 precedent) was rejected because it is an automatic retry of an unknown
storage failure: the faulted node keeps the longest log and tends to be re-elected, so leadership churns without
recovery. The risk is that a leader with a partially faulted WAL stays leader indefinitely while one peer cannot catch
up, which reduces fault tolerance by one. Error 74049, repeated every round with the peer endpoint, is the signal; the
operator fixes storage and calls `ResignAsync` or restarts the leader.

**Not changed.** No automatic recovery from unknown storage failures, no WAL logger, no new metrics.

## Scope and limitations

The review covered consensus transitions, replication and quorum handling,
read barriers and leases, membership changes, WAL persistence and recovery,
snapshot handling, and targeted transport security paths. It is not a formal
proof of Raft correctness or an exhaustive audit of every dependency.

Validation included existing targeted tests, in-memory probes of compiled
production methods, and real WAL restart probes. These do not constitute a
complete distributed fault-injection campaign. The security assessment did
not include live exploit reproduction against a network listener or
deployment-policy verification. The durable-write load baselines (#118 stage 2)
check the history and durability oracles under load on one machine; they do not
cover power loss, process kill or real networks.

The cache-configuration probe used `System.Runtime.Caching` 10.0.0.5, while the
built test output contains 10.0.0.11. Its results challenge the blanket claim of
silent degradation but are not exact-version validation of every setting.
No storage-sector query requiring elevated access or physical power-loss
experiment was performed.

Findings with conditional triggers identify those conditions above; no claim
is made that every issue occurs under every configuration.
