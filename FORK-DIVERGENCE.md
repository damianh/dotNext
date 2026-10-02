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

### Leader visibility after step-down
* **A leader that steps down stops reporting itself as leader straight away** (#65). When a leader steps down
  (higher term in a response or request, or resignation), the fork clears `Leader` before the state changes. Upstream
  keeps the local node as `Leader` until the node hears from the new leader or starts an election. In that window:
  * `Leader` (including `ICluster.Leader` and `IMessageBus.Leader`) returns `null`, and `LeaderChanged` fires with
    `null`. The sequence becomes old leader → `null` → new leader, where upstream goes from old leader straight to
    new leader.
  * `WaitForLeaderAsync` waits for the next leader. Upstream completes it with the stale local node.
  * The HTTP leader router returns 503 (Service Unavailable). Upstream handles the request locally on a node that is
    no longer leader.
  * `ApplyReadBarrierAsync` throws `QuorumUnreachableException` straight away. Before this fix it could spin
    synchronously on the calling thread until `Leader` changed or the token was canceled. A read barrier that runs
    while the local node is `Leader` but not yet in the leader state now yields and retries; it no longer spins.

### Cluster membership (#49, #52)
The TCP/UDP and HTTP hosts use the latest configuration entry in the log as the active configuration, as in Raft
(Ongaro's thesis, §4.1). Upstream adopts a configuration only after it is applied, which can elect two leaders in one
term (#49). See [Membership change semantics](RAFT-REVIEW.md#membership-change-semantics).
* **`Members` changes when a configuration entry is appended**, committed or not, on the leader and on followers.
  `MemberAdded` and `MemberRemoved` fire then. If the entry is later overwritten, the previous configuration in the log
  becomes active again and the events fire in reverse. Upstream changes `Members` when the entry is applied.
* **Restart rebuilds the configuration** from `IClusterConfigurationStorage` plus the configuration entries after it in
  the log. Installing a snapshot activates the configuration shipped with it.
* **`IClusterConfigurationStorage` holds the applied configuration only.** It is still written when a configuration
  entry is applied and still raises `ConfigurationChanged`, but the hosts no longer watch it: the polling loops that
  adopted a configuration on apply are removed.
* **`AddMemberAsync`/`RemoveMemberAsync`** (and the hosts' add and remove APIs) build a change only after the latest
  configuration in the leader's log and the leader's current-term no-op are committed and applied, then return once
  the change is committed and applied by the leader. Upstream waits for the leader's whole log to be applied first.
* **A failed or cancelled append or snapshot install rebuilds the active configuration at once** from the surviving log,
  so a partially overwritten configuration is never left active. Removing the last configured member is rejected
  (`RemoveMemberAsync` returns `false`), because an empty configuration cannot be committed.
* **A snapshot's configuration is persisted only after the snapshot is installed.** With the log-derived configuration,
  `InstallConfigurationAsync` stages the configuration in memory, and it reaches the storage after the snapshot append
  succeeds. A failed or aborted snapshot transfer therefore cannot advance the stored baseline past the log.
  Staged configurations are matched to a snapshot by sender term and the highest staged version not above the snapshot
  index, so overlapping snapshot requests cannot take each other's configuration. If the snapshot is durable but its
  configuration was not persisted (crash or storage failure), the leader's retransmission completes that second half
  and the node withholds the acknowledgment until it does. There is no atomic snapshot-plus-configuration write.
* **A leader that removes itself** keeps leading without counting itself until the removal is committed, then steps
  down to standby before `RemoveMemberAsync` returns.
* **A removed node may never learn of its removal** if it misses the entry. It keeps its old configuration; members
  reject its vote requests.
* **Warm-up (`CatchUpAsync`) needs an actual acknowledgment** (#52). A rejected or unsupported-version response no
  longer catches a new member up when the leader's commit index is 0.
* **A configuration entry enters a running leader's log only through the membership API** (#48). `ReplicateAsync`
  rejects configuration entries, and the public `ClusterConfigurationExtensions.AppendAsync` rejects the log of a
  started cluster that derives its configuration from the log. Upstream accepts both, and the leader then counts over a
  different member set than its followers. See
  [Configuration append paths](RAFT-REVIEW.md#configuration-append-paths).
* A `RaftCluster<TMember>` subclass that does not call `UseLogConfiguration` keeps the apply-time behaviour.
* **A cancelled snapshot install no longer faults the WAL** (#73) when the state machine opts in through
  `IStateMachine.IsSnapshotInstallCancellationSafe`; `SimpleStateMachine` does. Upstream faults the WAL on any
  exception from `IStateMachine.ApplyAsync` of a snapshot, including cancellation. `SimpleStateMachine` restores under
  its lifetime token instead of the request token. Other errors stay fail-closed. See
  [Snapshot install cancellation](RAFT-REVIEW.md#snapshot-install-cancellation-73).
* **A failed background snapshot no longer poisons `SimpleStateMachine`** (#75). Upstream keeps the faulted or cancelled
  `BeginSnapshottingAsync` task, so every later apply and snapshot install rethrows it and the WAL fails closed until it
  is reopened. The fork drops the failed attempt, keeps the previous snapshot, reports the failure through
  `SimpleStateMachine.OnSnapshotFailed`, and lets the next persist point start a new attempt. A failure of the final
  publish step (`writer.Commit()`) and cancellation by disposal are unchanged. A failure to roll back a snapshot that an
  incoming snapshot supersedes is dropped and reported the same way (#81), because nothing was published. See
  [Failed background snapshot](RAFT-REVIEW.md#failed-background-snapshot-75).
### Term and vote publication (#24)

`WriteAheadLog` no longer updates its in-memory term or vote before the `state`
record is durable. A failed or cancelled write leaves the published term and
vote unchanged, so a term is never acknowledged in an RPC reply before it
survives a restart. The persisted format is unchanged. See the "Supported
storage and crash model" section of `src\cluster\README.md`.

The directory is fsynced after the `state` file is first created (#82). On
filesystems and devices that honour directory fsync, this prevents a first-boot
term and vote from vanishing from the directory on power loss; macOS power-loss
durability remains unsupported as documented in `src\cluster\README.md`.

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
| `RaftCluster<TMember>.UseLogConfiguration` (protected) | absent | **added**: enables the log-derived active configuration (#49) |
| `IClusterConfigurationStorage<TAddress>.ReadConfigurationAsync` | absent | **added** (required interface member: implementations must decode configuration log entries; a breaking change for custom storages) |
| `RaftCluster<TMember>.ReplicateAsync` | accepts any entry | **throws `ArgumentException`** for an entry with `IsConfiguration == true` (#48) |
| `RaftCluster<TMember>.ReplicateAsync` (term) | appends an entry of any term | **throws `NotLeaderException`** when the entry's `Term` is not the term of the appending leader (and of the log, checked under the append lock), before anything is written (#50). Raw `IAuditTrail` appends stay unguarded. See [Leader proposal term safety](RAFT-REVIEW.md#leader-proposal-term-safety-50) |
| `ClusterConfigurationExtensions.AppendAsync(IPersistentState, IClusterConfiguration<TAddress>, CancellationToken)` | appends to any log | **throws `InvalidOperationException`** on the WAL or `ConsensusOnlyState` of a started cluster that uses `UseLogConfiguration` (#48) |
| `IStateMachine.IsSnapshotInstallCancellationSafe` | absent | **added** (default interface member, default `false`; `SimpleStateMachine` returns `true`): opts in to a snapshot `ApplyAsync` cancellation leaving the WAL usable (#73) |
| `SimpleStateMachine.OnSnapshotFailed(Exception)` (protected virtual) | absent | **added** (default: no-op): reports a failed background snapshot that was dropped instead of poisoning the state machine (#75) |

## Fork-only fixes
All of these are described in [RAFT-REVIEW.md](RAFT-REVIEW.md). Pull requests are in `damianh/dotNext`:
#1 (implementation review), #28 (deterministic failure tests), #29 (majority regression), #31 (quorum loss detection),
#33 (incoming snapshot rollback), #34 (invalidate leadership), #35 (consume metadata terminators), #36 (reject
unsupported WAL chunk sizes), #37 (lock upgrade deadlocks), #38 (complete flush target), #39 (test hangs/flakes),
#40 (flusher failure), #41 (snapshot flush alignment), #42 (applied index regression), #43 (restore no-op snapshot
before replay), #44 (leadership test flake), #45 (acknowledged log durability), #46 (membership lock), #47 (stale
configuration barriers), #59 (leader lease timing), #66 (read barrier spin after leader step-down), #68 (log-derived active configuration),
#69 (configuration append boundary), #70 (follower term signal reset per request), #50 (leader proposal term safety),
#73 (cancelled snapshot install), #75 (failed background snapshot), #24 (term/vote published after durable).

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
