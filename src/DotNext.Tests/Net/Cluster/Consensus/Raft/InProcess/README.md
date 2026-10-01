# In-process Raft test harness

`InProcessCluster` runs the production `RaftCluster<TMember>` state machine and
Raft RPC handlers without opening sockets. Use it for tests that need an exact
message schedule or virtual time; keep the HTTP and TCP suites for transport
integration coverage.

`ManualTimeProvider` delegates clock advancement and timer scheduling to
Microsoft's `FakeTimeProvider` from `Microsoft.Extensions.TimeProvider.Testing`.
It only adds timer callback lifetime tracking: the package's timers complete
`DisposeAsync` without waiting for an active callback, whereas Raft deadlines
must finish their callbacks before their cancellation sources are disposed.
The adapter suppresses callbacks after disposal and drains those already running.

Create one shared `ManualTimeProvider`, one `InProcessNetwork`, and a
`ConsensusOnlyState` or real write-ahead log for each node. Construct every node
with the same endpoint membership, start the nodes, and explicitly start the
chosen follower's election timer:

```csharp
var timeProvider = new ManualTimeProvider();
var network = new InProcessNetwork();
EndPoint[] membership =
    [new DnsEndPoint("a", 0), new DnsEndPoint("b", 0), new DnsEndPoint("c", 0)];
using var stateA = new ConsensusOnlyState();
using var stateB = new ConsensusOnlyState();
using var stateC = new ConsensusOnlyState();
await using var nodeA = new InProcessCluster(network, "a", membership, stateA,
    timeProvider, TimeSpan.FromMilliseconds(100), startFollower: false);
await using var nodeB = new InProcessCluster(network, "b", membership, stateB,
    timeProvider, TimeSpan.FromMilliseconds(100), startFollower: false);
await using var nodeC = new InProcessCluster(network, "c", membership, stateC,
    timeProvider, TimeSpan.FromMilliseconds(100), startFollower: false);
await nodeA.StartAsync(TestToken);
await nodeB.StartAsync(TestToken);
await nodeC.StartAsync(TestToken);
nodeA.StartElectionTimer();
timeProvider.Advance(TimeSpan.FromMilliseconds(100));
await nodeA.WaitForLeaderAsync(TimeSpan.FromSeconds(5), TestToken);
await nodeA.ForceReplicationAsync(TestToken);
await nodeA.WaitForLeadershipAsync(TestToken);
```

No fixed sleep is needed. Advancing the provider fires election, voting, and
heartbeat deadlines, while observable tasks and log indexes provide the test
oracles. The explicit replication above retries followers that reject the first
write-barrier heartbeat because their logs are one entry behind.
`Advance` moves the clock to the requested time and runs due timer callbacks
synchronously, not all thread-pool continuations. Zero-due timers can also fire
during creation or `Change`. Await a protocol milestone before advancing to the
next deadline; do not advance concurrently or inside a timer callback. Control
RPC delivery explicitly rather than relying on the order of equal deadlines.
Use the test runner's `--timeout` as a wall-clock deadlock guard, not to
schedule protocol events.
The cluster's init-only `TimeProvider` dependency defaults to
`TimeProvider.System`; it is not a bindable configuration option. HTTP hosting
resolves it from DI (`services.AddSingleton<TimeProvider>(timeProvider)`), falling
back to the system provider when none is registered. Directly constructed nodes
can inject it through the cluster initializer; the in-process constructor sets
it from its supplied provider and reuses it for replacements.

Call `Hold(source, target)` before an operation to queue traffic on a directed
link. Inspect `PendingMessages`, then call `DeliverAsync(message)` in any order
or `Drop(message)` to model a failed/lost RPC. `Release` stops holding future
sends; already queued messages still require explicit delivery or dropping.
Use `WaitForMessageAsync(source, target, type, token)` to await a queued RPC
without polling. `Partition` blocks new traffic, `Heal`
restores it, and the `bidirectional` argument distinguishes one-way failures
from complete partitions. Existing queued messages are unaffected by partitions.
For a black-holed request rather than an immediate failure, hold it until its
caller cancels or its protocol deadline expires.

`FailNext` faults one outbound RPC with the supplied exception; its returned
task observes that the fault was consumed. A fault on a leader's AppendEntries
RPC runs through the real replication worker's error handling, as demonstrated
by `ReplicationWorkerSurvivesInjectedFailure`. This is not process termination
or an arbitrary failure of every background task. Request cancellation is
cooperative, and a dispatched handler must finish cleanup before the sender's
task completes, so borrowed log-entry payloads remain alive.

The harness uses fixed membership. Configuration and snapshot installation
invoke the production handlers with the caller's configuration storage;
it does not emulate dynamic membership discovery or socket serialization.
`MembershipClusterFixture` (below) derives each node's members from its log.

`RestartAsync` always requires a callback that explicitly chooses the durable
state for the replacement:

```csharp
node = await node.RestartAsync(
    oldState => OpenWriteAheadLogAtTheSameLocation(oldState),
    TestToken);
```

The replacement receives fresh cluster members, replication cursors, pending
requests, leadership, and lifecycle tokens. The harness does not truncate or
reinterpret the supplied persistent state, so an acknowledged uncommitted tail
cannot be modeled as safely forgotten.

Persistent state is caller-owned: dispose it after its nodes, and explicitly
close/reopen a WAL in the callback when testing disk recovery. Retaining the
same `ConsensusOnlyState` tests runtime replacement, not disk durability.
Stopping either end of a request cancels and drains active handlers, fails held
messages, and prevents old source members or queued deliveries from crossing
into a replacement incarnation.

Run the harness with the existing Microsoft.Testing.Platform runner:

```powershell
dotnet run --project src\DotNext.Tests\DotNext.Tests.csproj --no-restore -- --filter-class 'DotNext.Net.Cluster.Consensus.Raft.InProcess.*' --progress off --timeout 90s
```

`CommitIndexTests` reproduces issue #3 with a real leader WAL and
`SimpleStateMachine` snapshot: five members have prior commit 7, only the leader
and one follower hold index 10, and a third member acknowledges snapshot 6 from
the current term. Responsive quorum is not enough to commit 10. The leader
selects the majority-th largest replicated index using the full membership.

`MembershipCommitTests` covers 3/5/7 members, unavailable replies arriving before
or after quorum, current- and previous-term snapshot acknowledgments, successful
catch-up, and commit-index monotonicity. These schedules hold election and
replication RPCs from the start, observe the automatic first round before forcing
its retry, and account for every worker's setup RPC before beginning another
round. A completed quorum alone does not prove that a lagging worker has finished
its earlier rounds.

`AcknowledgedLogDurabilityTests` exercises issue #2 with three real WAL-backed
nodes. After a durable prefix, only F1 acknowledges N while retaining commit
N-1. The leader commits/persists N and completes the client operation; no later
commit notification reaches F1. With the leader unavailable, reopening F1 must
preserve N, reject stale F2's election, and still permit F1 to win and replicate
the preserved payload. The process-termination variant kills a child hosting
the cluster at the acknowledged milestone and reopens F1/F2; L remains offline.

`WriteAheadLogDurabilityTests` includes an explicit-tail-page-flush control and
the memory/flush/hash restart matrix. `WalCrashWorker` is a test-only child entry
point: parents select it in the existing MTP executable and use a unique named
pipe to observe acknowledgment before terminating that specific process.
Ordinary test discovery skips the worker. Process termination bypasses WAL
disposal but does not simulate loss of the operating system's page cache.

```powershell
dotnet run --project src\DotNext.Tests\DotNext.Tests.csproj --configuration Debug --no-restore -- --filter-class 'DotNext.Net.Cluster.Consensus.Raft.InProcess.AcknowledgedLogDurabilityTests' --filter-class 'DotNext.Net.Cluster.Consensus.Raft.StateMachine.WriteAheadLogDurabilityTests' --progress off --timeout 120s
```

`QuorumLossTests` covers issue #5 with valid two- and four-member clusters.
After a healthy election and write-barrier retry, it supplies every response
with exactly half the membership unavailable, counting the local leader among
the responsive half. Forced replication must finish with `NotLeaderException`,
leadership must end, and shutdown/disposal must finish. Additional cases cancel
one caller without stranding another and stop/dispose a leader with held RPCs.
`InProcessClusterFixture` shares membership/election setup with the commit tests;
its bounded cleanup keeps a lifecycle regression from hanging the test runner.

The accompanying `ReplicationBarrierTests` assert completion synchronously after
the final contribution, without a sleep or cancellation oracle. They also cover
odd-sized clusters, early failure, response ordering, touched/canceled/higher-term
outcomes, the overflow buffer, and reuse after late replies. Run both layers with:

```powershell
dotnet run --project src\DotNext.Tests\DotNext.Tests.csproj --no-restore -- --filter-class 'DotNext.Net.Cluster.Consensus.Raft.ReplicationUtils.*' --filter-class 'DotNext.Net.Cluster.Consensus.Raft.InProcess.QuorumLossTests' --progress off --timeout 90s
```

## Membership changes

`MembershipClusterFixture` runs five WAL-backed voters and one joiner, each with
`InMemoryClusterConfigurationStorage`, on a network where every link is held.
Nothing progresses on its own:

- `ElectAsync(candidate, passWriteBarrier, filter)` delivers only pre-votes and votes,
  then optionally replicates until the new leader's no-op is applied. Pass
  `false` to leave an inherited tail unapplied. The optional filter applies to
  both phases, for example to elect a leader inside one side of a partition.
- `PumpAsync(source, operation, filter)` handles the source's RPCs until the
  operation completes. The filter returns `Deliver`, `Drop`, or `Hold`. For a
  leader it keeps one forced round in flight, so rejected appends are retried
  without heartbeat deadlines.
- `PumpAllAsync(operation, filter, leader)` handles RPCs from every node
  without awaiting each delivery. A leader answering a follower's read
  barrier waits for its own replication round, so a sequential pump would
  deadlock. When `leader` is set it keeps one of that node's forced rounds in
  flight.
- `ReplicateOnlyToAsync(leader, follower, index)` gives the entries to one
  follower and then drops the rest, so the leader steps down with the entries
  uncommitted. It holds the other RPCs until the follower has the entries,
  because dropping them first makes the leader step down and cancel the
  follower's in-flight append. An RPC that cannot carry the entries (an empty
  heartbeat, or a round that started before the entries were appended) is
  delivered to everyone, because a held one would keep that round from
  completing. The in-process network records the last entry index of each
  append (`PendingMessage.LastEntryIndex`) for this purpose.
- Each node derives its member list from the latest configuration entry in
  its log as soon as the entry is appended, committed or not, as in production
  (`RaftCluster.UseLogConfiguration`). No polling step is needed.
- `RestartAsync(index)` replaces a node with a new instance over the same WAL
  location and configuration storage, so the active configuration is rebuilt
  from the storage and the log. `MembershipClusterFixture(voterCount, joinerCount)`
  changes the cluster size.
- `DetectAsync` invokes the production unavailable-member callback (under
  `membershipLock`). `AppendRemovalAsync` appends an unreplicated removal
  directly, through the internal storage-level append, to model a change that
  reached only part of the cluster. The public
  `ClusterConfigurationExtensions.AppendAsync` rejects a running node's log.

`MembershipConfigurationTests` covers issue #18: a manual add/remove, a second
detection, and a change by a new leader must each preserve an unapplied removal.
It also covers the stale-term detection and single-entry appends.
`ConfigurationAppendBoundaryTests` covers #48: `ReplicateAsync` and the public
`AppendAsync` extension cannot put a configuration into a running leader's log
behind the membership API, and a node rejoins when its uncommitted removal is
overwritten.
`TermGuardedAppendTests` covers the log-level term guard for the WAL and
`ConsensusOnlyState`.

`MembershipWarmUpTests` covers issue #54. A removed node that missed its own removal
appends the same entry, so it still matches the leader's log. Re-adding it must complete
warm-up after one empty heartbeat, which the node acknowledges as `Replicated`. No new
application write is needed. A lagging joiner is added only after it holds the
committed prefix, and a joiner that rejects every round is not added.
`ReplicationUtils.ReplicationProcessCatchUpTests` drives `CatchUpAsync` with scripted
responses: an empty heartbeat, snapshot catch-up, rejection, unsupported version,
higher term, an unavailable member, and cancellation. A rejection is never enough to
catch up, even when the committed prefix is empty (#52).

`LaggingCandidateElectionTests` covers issue #49 (CE-2): a candidate whose log holds a
configuration that removes a member counts votes against that configuration, so the
nodes of the previous configuration cannot elect a second leader in the same term.
`LogDerivedConfigurationTests` covers the rest of the log-derived configuration: a
truncated, uncommitted configuration is reverted; a restart rebuilds the configuration
from the storage and the log; a leader that removes itself steps down once the removal
is committed; and a leader never commits under a configuration it has already replaced
(CE-1).

```powershell
dotnet run --project src\DotNext.Tests\DotNext.Tests.csproj --configuration Debug --no-restore -- --filter-class 'DotNext.Net.Cluster.Consensus.Raft.InProcess.MembershipConfigurationTests' --filter-class 'DotNext.Net.Cluster.Consensus.Raft.InProcess.MembershipHarnessTests' --filter-class 'DotNext.Net.Cluster.Consensus.Raft.InProcess.MembershipWarmUpTests' --filter-class 'DotNext.Net.Cluster.Consensus.Raft.ReplicationUtils.ReplicationProcessCatchUpTests' --filter-class 'DotNext.Net.Cluster.Consensus.Raft.TermGuardedAppendTests' --filter-class 'DotNext.Net.Cluster.Consensus.Raft.InProcess.LaggingCandidateElectionTests' --filter-class 'DotNext.Net.Cluster.Consensus.Raft.InProcess.LogDerivedConfigurationTests' --progress off --timeout 180s
```

## Follower read barriers

`FollowerReadBarrierTests` covers issue #19: a follower's strong read barrier
must obtain a read index that the leader has confirmed with a quorum.

- A five-voter cluster is partitioned into {0, 1} and {2, 3, 4}. The majority
  elects node 2 and commits a newer write. The read on node 1 then goes to the
  former leader, node 0. That leader must not authorize its stale commit
  index, whether or not it has observed the new term.
- A new leader whose commit index predates an entry committed in an earlier
  term must return a read index that covers the entry, because the index is
  floored at the current-term write barrier.
- A healthy read waits for a quorum round. A leader without a quorum returns
  `null` and steps down. A cancelled read does not strand the leader.
- A follower lagging behind a snapshot waits until it applies the snapshot
  up to the confirmed read index.

```powershell
dotnet run --project src\DotNext.Tests\DotNext.Tests.csproj --no-restore -- --filter-class 'DotNext.Net.Cluster.Consensus.Raft.InProcess.FollowerReadBarrierTests' --progress off --timeout 180s
```

## Leader lease timing

`LeaderLeaseTimingTests` covers issue #20. The tests check the supported lease
clock and timer model. A lease is usable when `TryGetLeaseToken` returns a
token that is not cancelled. Every scenario establishes the lease through a
completed quorum round. See [Leader lease activation](#leader-lease-activation)
for the conditions a new leader must meet first.

- `InProcessCluster.LeaseOptions` enables leases with an optional
  `ClockDriftBound`. `InProcessClusterFixture` accepts these options and a
  per-node clock factory, and `RestartAsync(member)` replaces a node while
  keeping its persistent state.
- `DriftingTimeProvider` makes a node's monotonic clock and timers run slower
  than the shared clock by a constant factor. Drift within the configured
  bound keeps the lease inside the voters' stickiness window. Drift beyond it
  produces the expected overlap, which is kept as a characterization of an
  unsupported configuration.
- `StarvableTimeProvider` queues a node's timer callbacks while its clock
  keeps advancing. This models thread-pool starvation or a GC pause that
  delays the lease timer.
- `InProcessNetwork.DeliverAndLoseResponseAsync` runs the target handler and
  then fails the sender, as if the response was lost.

`RetransmittedSnapshotAcknowledgmentKeepsVoterSticky` covers #58: a leader
retransmits a snapshot after the follower's acknowledgment was lost.
`VoteStickinessTests.AlreadyInstalledSnapshotRefreshesStickiness` covers the
follower side of the same fix. `VoteStickinessTests.StartupSuppressesVotingWhenLeaseIsEnabled` covers
the startup vote suppression that makes voter restarts safe.

```powershell
dotnet run --project src\DotNext.Tests\DotNext.Tests.csproj --no-restore -- --filter-class 'DotNext.Net.Cluster.Consensus.Raft.InProcess.LeaderLeaseTimingTests' --filter-class 'DotNext.Net.Cluster.Consensus.Raft.InProcess.VoteStickinessTests' --progress off --timeout 180s
```

## Leader lease activation

`LeaderLeaseActivationTests` covers issue #21. A new leader must not expose a
usable lease until a majority has confirmed its term and its own state machine
has applied the current-term write barrier.

- The leader uses a `WriteAheadLog` whose state machine blocks on a gate. The
  WAL applies the payload-less write barrier without calling the state
  machine, so the leader first inherits a payload entry from an earlier term.
  Blocking that entry holds back application of the barrier.
- `LeaseRequiresQuorumAndAppliedWriteBarrier` checks each interval before
  activation: before any quorum round, after quorum confirmation but before
  the barrier is committed, and after commit but before it is applied. It then
  releases the gate, waits for the lease, and checks that expiry and renewal
  still work.
- Other scenarios: a dropped first round, a failed replication worker,
  step-down or shutdown before activation, and disabled leases.

```powershell
dotnet run --project src\DotNext.Tests\DotNext.Tests.csproj --no-restore -- --filter-class 'DotNext.Net.Cluster.Consensus.Raft.InProcess.LeaderLeaseActivationTests' --progress off --timeout 180s
```

## Term and vote durability

`TermVoteDurabilityTests` covers issue #24. The target node is WAL-backed, and
a "crash" is a dispose and reopen of the WAL followed by a node restart.
`NodeStateFault` makes the `state` write fail by swapping the file handle for
a read-only one (test-only reflection).

- H1 and H2: a failed or cancelled vote write grants nothing, and a failed write
  never leads to two grants in one term across a restart.
- H3: a term acknowledged in an `AppendEntries` reply survives a restart, so a
  stale leader cannot overwrite an acknowledged entry. Triggered by a failed
  term update from `AppendEntries` and from a `Vote` request.

```powershell
dotnet run --project src\DotNext.Tests\DotNext.Tests.csproj --no-restore -- --filter-class 'DotNext.Net.Cluster.Consensus.Raft.InProcess.TermVoteDurabilityTests' --progress off --timeout 180s
```
## Seeded simulation (#56 stage 1)

`SimulationTests` runs a seeded, bounded schedule of faults and client proposals against a fixed cluster of 3 or 5
voters (`Simulation.cs`, `SimulationHistory.cs`). It reuses `InProcessCluster` and `InProcessNetwork`; there is no
second Raft implementation. Nodes are backed by `WriteAheadLog` (private memory, no compaction), each with its own
directory, so a restart is meaningful: stop the node, dispose the WAL, reopen it at the same location.

**What is simulated.** A fault phase of 150 weighted steps chosen from: deliver a pending message, deliver and lose
the response, drop a message, advance the shared `ManualTimeProvider`, propose through
`IRaftCluster.ReplicateAsync`, partition a pair of nodes, heal a pair, crash a node (at most 3 times per run) and
recover a crashed node. All links start held, so messages move only when the schedule delivers them. There are no
membership changes, reads, leases, snapshots or compaction, I/O faults, or real transports.

**What is controlled.** The seed decides every step, the per-node election timeouts (fixed per node, so the
production timer has no randomness), the order of deliveries, and the timing of crashes. Time is manual. Delays and
drops go through `InProcessNetwork`'s hold, deliver and drop controls.

**What is not controlled.** Thread-pool scheduling inside a step (continuations of a delivered handler), the wall-clock
`SettleAsync` heuristic that decides when a step has gone quiet (it stops after two stable 1 ms windows, at most 50 ms), and the
term read in the `LeaderChanged` handler, which can race with a concurrent term change. **A seed alone is therefore
not a guarantee of replay.** The recorded trace of actual decisions is the record of what happened, and it is
printed with the seed, the cluster size and the git revision on every failure.

**Oracles** (`SimulationHistory`, checked at every checkpoint: every 10 steps, before a crash and at the end):

1. Election safety: at most one leader per term, from `LeaderChanged` plus the term at that moment.
2. Committed-prefix agreement: an index committed on two nodes (or twice on one node, over time) has the same term and payload.
3. Acknowledged writes are preserved: every `ReplicateAsync` that returned success is at its index, with its payload,
   in every committed log that covers that index, and in some node's committed log at the end. A client history has three
   outcomes: acknowledged, rejected (`NotLeaderException` before the append), and unknown (cancelled, or a `NotLeaderException`
   after the append). Only acknowledged writes are held to oracle 3.

**Liveness is separate.** After the fault phase every link is healed, every node is up, pending messages are delivered
and time advances. A leader must be elected and one new proposal must commit within 400 iterations. A failure is reported
as `LIVENESS failure`, distinct from `SAFETY failure`.

**Checking the checker.** Pure oracle tests (`SimulationTests`, the synchronous facts) feed each oracle a synthetic bad
history and assert that it fails.

**Replay.** The failure message contains the seed and the cluster size, and the first trace line also shows the steps
(set `DOTNEXT_RAFT_SIM_STEPS` too if they are not the default 150). Try it again with:

```powershell
$env:DOTNEXT_RAFT_SIM_SEED = '<seed>'; $env:DOTNEXT_RAFT_SIM_VOTERS = '<3 or 5>'
dotnet run --project src\DotNext.Tests\DotNext.Tests.csproj -- --filter-method '*SimulationTests.Campaign' --progress off
```

**CI set.** `FixedSeedKeepsSafetyAndRecoversLiveness` runs seeds 1 to 8 for each size (16 runs, about 10 seconds in Debug):

```powershell
dotnet run --project src\DotNext.Tests\DotNext.Tests.csproj -- --filter-class 'DotNext.Net.Cluster.Consensus.Raft.InProcess.SimulationTests' --progress off
```

**Campaign.** `Campaign` is skipped unless an environment variable is set. `DOTNEXT_RAFT_SIM_SEEDS=N` runs N random
seeds for each size (roughly 0.7 s per run); `DOTNEXT_RAFT_SIM_STEPS` changes the fault-phase length. The N seeds are
derived from `DOTNEXT_RAFT_SIM_BASE_SEED` (random when unset or empty; a malformed value fails the run). The base seed is always printed to the console as
`campaign base seed=B seeds=N`, so the same base seed and count give the same seeds again. The first failure stops the
run and prints the seed and trace.

```powershell
$env:DOTNEXT_RAFT_SIM_SEEDS = '200'
dotnet run --project src\DotNext.Tests\DotNext.Tests.csproj -- --filter-method '*SimulationTests.Campaign' --progress off
```

**Nightly campaign.** `.github/workflows/raft-simulation.yml` runs the `SimulationTests` class (the fixed seeds, the
oracle facts and the campaign) on `ubuntu-latest` and `windows-latest`:

- nightly (`schedule`, from the default branch) with 300 seeds per size, both sizes, default steps and a random base seed;
- on demand (`workflow_dispatch`) with the inputs `seeds`, `voters` (`both`, `3` or `5`), `steps` and `base_seed`, for
  example `gh workflow run raft-simulation.yml --ref <branch> -f seeds=50 -f voters=5`;
- on pull requests that change the workflow file, with 5 seeds.

The job has a 60-minute timeout (45 minutes for the test step). Each run writes its parameters (base seed, seeds,
voters, steps, commit) to the job summary. On failure the job summary lists every failing seed with its category,
cluster size and steps. It also gives the replay command for the first failing seed and for the whole campaign
(`DOTNEXT_RAFT_SIM_BASE_SEED` + `DOTNEXT_RAFT_SIM_SEEDS`). The console log and the TRX file, which contain the trace,
are uploaded as the `raft-simulation-<os>-<attempt>` artifact. Check out the commit shown in the summary before you replay.

Compaction is disabled on purpose (`IStateMachine.CreateNoOp` with a large threshold): a compacted prefix reads back
as one empty term-0 snapshot entry, which does not map to log indexes.