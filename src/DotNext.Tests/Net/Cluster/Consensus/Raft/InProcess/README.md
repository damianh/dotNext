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
`MembershipClusterFixture` (below) adopts applied configurations explicitly.

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
  follower's in-flight append.
- The applied configuration reaches a node's member list only through
  `MembershipNode.PropagateConfigurationAsync`, the same path the production
  polling loop takes. This keeps "applied" and "adopted" as separate,
  controllable steps.
- `DetectAsync` invokes the production unavailable-member callback (under
  `membershipLock`). `AppendRemovalAsync` appends an unreplicated removal
  directly, to model a change that reached only part of the cluster.

`MembershipConfigurationTests` covers issue #18: a manual add/remove, a second
detection, and a change by a new leader must each preserve an unapplied removal.
It also covers the stale-term detection and single-entry appends.
`TermGuardedAppendTests` covers the log-level term guard for the WAL and
`ConsensusOnlyState`.

`MembershipWarmUpTests` covers issue #54. A removed node that received and applied
its own removal still matches the leader's log. Re-adding it must complete warm-up
after one empty heartbeat, which the node acknowledges as `Replicated`. No new
application write is needed. A lagging joiner is added only after it holds the
committed prefix, and a joiner that rejects every round is not added.
`ReplicationUtils.ReplicationProcessCatchUpTests` drives `CatchUpAsync` with scripted
responses: an empty heartbeat, snapshot catch-up, rejection, unsupported version,
higher term, an unavailable member, and cancellation.

```powershell
dotnet run --project src\DotNext.Tests\DotNext.Tests.csproj --configuration Debug --no-restore -- --filter-class 'DotNext.Net.Cluster.Consensus.Raft.InProcess.MembershipConfigurationTests' --filter-class 'DotNext.Net.Cluster.Consensus.Raft.InProcess.MembershipHarnessTests' --filter-class 'DotNext.Net.Cluster.Consensus.Raft.InProcess.MembershipWarmUpTests' --filter-class 'DotNext.Net.Cluster.Consensus.Raft.ReplicationUtils.ReplicationProcessCatchUpTests' --filter-class 'DotNext.Net.Cluster.Consensus.Raft.TermGuardedAppendTests' --progress off --timeout 180s
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