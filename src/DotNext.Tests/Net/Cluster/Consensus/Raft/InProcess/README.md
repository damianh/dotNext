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

The base harness uses fixed membership. Configuration and snapshot installation
invoke the production handlers with the caller's configuration storage;
it does not emulate dynamic membership discovery or socket serialization.
`MembershipClusterFixture` opts into dynamic membership with real WALs and
configuration storage. Its node subclass queues committed configuration events
before applying the production configuration-change scope, matching the default
and HTTP clusters. Existing fixed-membership fixtures are unchanged.

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

## Membership ownership and configuration ordering

`MembershipLockTests` covers issue #17. An elected, WAL-backed leader runs the
real unavailable-member dispatcher and configuration-removal helper. The test
awaits the dispatcher task, observes caller-identity cleanup, and then completes
a manual add or removal. Read-only private-field accessors observe the actual
leader identity and lock ownership; they never install a state or release a lock.
The primary unfixed failure is `ConcurrentMembershipModificationException`
after the dispatcher has completed, not an arbitrary membership timeout.

Additional schedules cover canceled acquisition without releasing another
owner's lock, cancellation/exception after acquisition, real resignation while a
callback owns the lock, stale queued caller validation, and membership progress
after reelection. A controlled detector response runs the actual replication
worker-to-leader-to-callback path. Its notification-only override isolates lock
serialization from extra notifications caused by configuration replication.
Disposal-race cases hold a callback until synchronous or asynchronous disposal
has destroyed the membership lock, then allow it to complete or throw. Cleanup
tolerates only lock-release disposal exceptions while the cluster is disposing
or disposed; callback failures still reach the existing error logger.

`MembershipConfigurationTests` covers issue #18 on the independently repaired
#17 baseline. Held RPCs keep a detector's removal unapplied until a subsequent
manual add/remove crosses its barrier. The inherited-configuration case sends
the removal to only one follower in a five-member cluster, stops the leader
before commitment, and elects that follower through real pre-votes/votes.
Manual add/remove and automatic removal must preserve the inherited removal.
The automatic test uses the leader's `ConsensusToken`, as the real dispatcher
does internally; the public `LeadershipToken` is deliberately canceled until
the newly elected leader's write barrier commits.

The oracles check the exact final member set, configuration version, WAL applied
index, and live membership. Controls cover already-applied removal, consecutive
automatic removals, explicit re-add with a real catch-up suffix, unchanged no-op
checks, revalidation after a barrier, and caller cancellation during the barrier
for add/remove/detection. `PumpAsync` delivers held messages until an observed
operation completes, without advancing virtual time or fabricating a log.
Competing election traffic stays held in the inherited-configuration schedule.

```powershell
dotnet run --project src\DotNext.Tests\DotNext.Tests.csproj --configuration Debug --no-restore -- --filter-class 'DotNext.Net.Cluster.Consensus.Raft.InProcess.MembershipLockTests' --progress off --timeout 90s
dotnet run --project src\DotNext.Tests\DotNext.Tests.csproj --configuration Debug --no-restore -- --filter-class 'DotNext.Net.Cluster.Consensus.Raft.InProcess.MembershipConfigurationTests' --progress off --timeout 120s
```

The baseline failures and issue #18 release-gate disposition are recorded with
finding 6 in the repository's `RAFT-REVIEW.md`.
