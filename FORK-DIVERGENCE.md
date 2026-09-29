# Fork divergence from upstream

This fork (`damianh/dotNext`, default branch `fork`) tracks [dotnet/dotNext](https://github.com/dotnet/dotNext)
`master` but carries Raft/WAL correctness and durability fixes that upstream does not have (see
[RAFT-REVIEW.md](RAFT-REVIEW.md)). This document records **where the fork intentionally behaves differently from
upstream** and logs every upstream sync. Keep it up to date whenever a fork change diverges from upstream or an
upstream sync is merged.

## Behavioural differences

### Write-ahead log durability
* **Appends are always durable before they are acknowledged.** The fork persists appended pages and the checkpoint
  before an append completes (fork #45). Upstream 6.8.0 has a lazy flusher, and its `WriteAheadLog.Options.FlushOnCommit`
  option (default `true`) decides whether the flusher runs on every commit.
* **`WriteAheadLog.Options.FlushOnCommit` does not exist in the fork.** It would be redundant, because durability no
  longer depends on flushing at commit time. Upstream code that sets this property will not compile against the fork.
* **Dispose does not flush.** Upstream 6.8.0 does a final foreground flush in `DisposeAsync`, so its commit index
  survives a restart without an explicit flush. The fork keeps its own disposal contract (#38/#40): disposal fails
  pending flush callers with `ObjectDisposedException`, waits only for the flush pass that is already running, and
  then releases resources. Call `FlushAsync` before disposing if the commit and applied indices must survive a restart.
  Appended entries are durable either way, and a commit index that is lost is learned again from the leader.

### Checkpoint on-disk format
| Version | Origin | Layout | Fork support |
|---|---|---|---|
| 0 | upstream ≤ 6.7 | 8 bytes (commit index) | Read, then upgraded one-way |
| 1 | upstream 6.8.x | 20 bytes (version, commit index, last index) | Read, then upgraded one-way |
| 2 | fork | 192 KiB: three checksummed 64 KiB generational slots (commit index, last index, write position, snapshot index, generation) plus the `checkpoint.format`, `checkpoint.pending` and `checkpoint.prepared` sidecars and the `overwrite` journal | Native |

* The fork's format was first numbered `1` and was renumbered to `2` during the 6.8.1 sync, because upstream's new
  format also claimed version `1`. The fork has no deployments, so no stores that used the old number exist.
* A store written by upstream 6.8.x can be opened by the fork when its last index is at least its commit index.
  Its last index is trusted as durable, because upstream writes it only after the pages have been flushed. The first
  durable update upgrades the store to version 2, and upstream cannot read it after that.
* Version 0 migration assumes the same metadata page size that upstream 6.8.1 uses when it opens legacy stores:
  4 KiB. Version 0 stores written on hosts whose OS page size was larger than 4 KiB are not supported
  migration inputs, matching upstream 6.8.1.
* Upstream keeps no metadata record at a snapshot boundary (for example after snapshot catch-up), so for version 0 and
  1 stores whose last index is covered by the restored snapshot, the fork rebuilds the boundary record even if the
  metadata page already exists. Stale legacy boundary slots are ignored; the rebuilt boundary is placed after the
  highest existing data page so obsolete data is not overwritten during migration. Version 2 stores still require it
  and are rejected without it.
* Checkpoints with unknown versions, with a length that does not match their version, with negative indices, or with
  an upstream version-1 last index below its commit index are rejected with `IntegrityException`.

### WAL internals kept from the fork (upstream versions were rejected)
* **Cleaner:** snapshot boundary metadata is kept valid by `WriteSnapshotBoundary` (fork #41). Upstream's
  `lastWrittenIndex` guard is not used.
* **Applier:** monotonic applied index (fork #42), which covers upstream's equivalent change.
* **Flusher:** the fork's flusher keeps its failure-propagation behaviour (#38 and #40). Upstream's
  `FlushState`/`UnflushedIndex` rework is not used.

### Leader leases
* **Lease validity is checked against the monotonic clock.** `TryGetLeaseToken` compares the time provider's
  timestamp with the lease deadline and cancels an expired lease, even if its timer callback has not run yet (#20).
  Upstream relies only on the timer, so a delayed callback extends the lease.
* **Lease-enabled nodes refuse to vote for one election timeout after start** (#20). This keeps a restarted voter from
  helping elect a new leader while a lease it acknowledged before the crash is still valid. The first election after
  a cold start can take up to one election timeout longer than upstream. Nodes with leases disabled behave as upstream.
  See [Leader lease timing model](RAFT-REVIEW.md#leader-lease-timing-model).
* **A new leader's lease starts inactive** (#21). `TryGetLeaseToken` returns `true` with a canceled token until a
  majority has confirmed the leader's term and the local state machine has applied the current-term write barrier.
  Upstream issues a usable lease as soon as the node becomes leader.

### Leader proposal term safety
* **`RaftCluster.ReplicateAsync` rejects entries whose term is not the leader's term** (#50). A stale or future term
  throws `NotLeaderException` and nothing is appended. The append runs through the term guard, so it is also rejected
  under the append lock if the term advanced after the check. Upstream appends the entry with the caller-supplied term.
  `Replicate*Async` helpers stamp the term and are not affected.
* **`ClusterConfigurationExtensions.AppendAsync(IPersistentState, ...)` is term-guarded** (#50). It can no longer land
  after the term advanced between sampling and appending. It is a low-level storage operation, not a proposal.
* **`ReplicateAsync` never acknowledges an overwritten entry** (#50). After the wait for the apply it throws
  `NotLeaderException` if the leader token is cancelled, because a newer leader can overwrite the entry and advance the
  applied index past it. Upstream returns as soon as the index is applied.
* **A cancelled or failed proposal has an unknown outcome** (#50, #53). The entry may still commit. Use an application
  idempotency key or check the log before retrying. The bounded request journal (#25) covers transport retries only.
* No public signatures changed. `ITermGuardedAuditTrail` remains internal. Custom `IPersistentState` implementations
  get a best-effort check only.
See "Leader proposal term contract" in [RAFT-REVIEW.md](RAFT-REVIEW.md).

### Direct I/O page checks
* On Linux, `LinuxDirectPageManager.IsAllowed` checks `pageSize % sectorSize == 0`. Upstream 6.8.1 has the operands
  inverted (`sectorSize % pageSize`), which does not match the constructor's own validation. The fork fixed this.
* The metadata page size is a constant 4 KiB, as in upstream 6.8.1 (portable to ARM64 hosts with 16 KiB pages).

### Delegate function-pointer equality
* In the non-JIT function-pointer delegate fallback, open and closed delegate targets are not equal unless
  their runtime types match. Upstream 6.8.1 compares the base target to any derived target by pointer
  alone, which is asymmetric with the closed-delegate target comparison.

## API differences
| API | Upstream | Fork |
|---|---|---|
| `WriteAheadLog.Options.FlushOnCommit` | present (6.8.0+) | **removed** |
| `DotNext.IO.Log.ILogCompactionSupport` | removed in 6.8.0 (breaking change in a minor release) | removed too (follows upstream) |

## Fork-only fixes
All of these are described in [RAFT-REVIEW.md](RAFT-REVIEW.md). Pull requests are in `damianh/dotNext`:
#1 (implementation review), #28 (deterministic failure tests), #29 (majority regression), #31 (quorum loss detection),
#33 (incoming snapshot rollback), #34 (invalidate leadership), #35 (consume metadata terminators), #36 (reject
unsupported WAL chunk sizes), #37 (lock upgrade deadlocks), #38 (complete flush target), #39 (test hangs/flakes),
#40 (flusher failure), #41 (snapshot flush alignment), #42 (applied index regression), #43 (restore no-op snapshot
before replay), #44 (leadership test flake), #45 (acknowledged log durability), #46 (membership lock), #47 (stale
configuration barriers), #59 (leader lease timing).

## Upstream sync log

### 6.7.1 → 6.8.1 (upstream `34ebb0cdb`, 69 commits)
Merge base `d46d29859` (Release 6.7.1).

**Adopted as-is**
* Core: `Atomic<T>` field reads, `AdvancedHelpers`/`Span.ReadOnly`/`DelegateHelpers` performance and AOT work,
  `SparseBufferWriter` fix, `UserDataStorage`, `AppContextExtensions`.
* IO: `UnbufferedFileStream` flush via function pointers. `ILogCompactionSupport` was removed.
* HTTP transport (dotnet/dotNext#299): tolerant parsing of the state version, last index and command ID headers,
  for rolling upgrades.
  `X-Raft-State-Version` and `X-Raft-Last-Index` fall back only when absent; malformed present values are protocol
  errors.
* `RaftClusterMember.ResignAsync` and `GetMetadataAsync` became public, and `TryGetMetadata()` was added.
* A removed member can rejoin the cluster (`FreezeAsync`, resumable standby, readiness probe reset). It is merged
  with the fork's membership lock (#46).
* RequestVote §5.4.1 up-to-date check (dotnet/dotNext#298). It is identical to the fork's logic, and upstream's
  version was kept.
* Constant 4 KiB metadata pages, plus `IsAllowed` direct-I/O guards (with the Linux fix above).
* AOT test expansion, dependency bumps and the version bump to 6.8.1.

**Superseded by the fork's design**
* Upstream checkpoint version 1: the fork's format became version 2, and upstream v1 is read as a legacy migration
  input.
* `FlushOnCommit` and the flusher rework: dropped.
* Flush on dispose: rejected, because it conflicts with the fork's disposal contract (see above).
* Cleaner `lastWrittenIndex`: the fork's snapshot-boundary handling was kept.
