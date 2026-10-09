# Raft fault campaign (#118 stage 3)

A real-process fault-injection smoke campaign for the fork's Raft implementation. A 3-node cluster runs as three
separate OS processes on loopback, over the HTTP or the TCP transport. The campaign kills and restarts nodes, and
partitions one node away from the other two and heals it, under a closed-loop write load, and checks safety, durability
and liveness oracles after each fault. Background, results and
blind spots: [RAFT-REVIEW.md, "Real-process fault campaign, #118 stage 3"](../../RAFT-REVIEW.md#real-process-fault-campaign-118-stage-3).

Linux only. The default `WriteAheadLog` memory strategy only. No production code is involved beyond the library
itself; the node host is test-only.

## Running

```bash
dotnet build src/DotNext.Raft.FaultCampaign -c Release
cd src/DotNext.Raft.FaultCampaign
./bin/Release/net10.0/DotNext.Raft.FaultCampaign run --transport http --seed 1 --out /tmp/fc/http
./bin/Release/net10.0/DotNext.Raft.FaultCampaign run --transport tcp  --seed 1 --out /tmp/fc/tcp
```

A run takes about 65 seconds. The seed picks the victim followers and the hold times; the order of faults is fixed, so
every run covers every fault. Re-running with the same seed and transport repeats the same schedule, though not the
same interleaving (real processes and real time).

| Option | Default | |
|---|---|---|
| `--transport http\|tcp` | `http` | the Raft transport |
| `--out <dir>` | `./fault-campaign` | the artifact directory; its `logs`, `data` and `claims` subdirectories are recreated |
| `--seed <n>` | 1 | victims and hold times |
| `--episodes <list>` | all | a comma-separated subset of the schedule, in schedule order |
| `--inject none\|drop-applied\|volatile-storage\|partition-leak` | `none` | a test-only failure the oracles must catch |
| `--snapshot-interval <n>` | 50 | entries between state machine snapshots |
| `--payload <bytes>` | 256 | write size |
| `--clients <n>` | 4 | closed-loop clients |
| `--recovery-timeout <s>` | 30 | bound on each recovery and catch-up wait |
| `--max-duration <min>` | 10 | bound on the whole run, from the driver's start to the verdict; every wait and hold stops at it (exit 5). Stopping the nodes afterwards (up to 15 s) is not included |
| `--keep-data true\|false` | `false` | keep the node data directories after a passing run |

### Exit codes

| Code | Verdict | Meaning |
|---|---|---|
| 0 | pass | every episode ran and every oracle held |
| 1 | harness error | a bug or environment problem in the tool itself |
| 2 | usage | bad command line |
| 3 | safety violation | an oracle failed: see `report.json` `.violation` |
| 4 | liveness failure | the cluster did not recover within `--recovery-timeout` |
| 5 | incomplete | the run hit `--max-duration`, ran low on disk, or a fault did not take effect (no snapshot installed) |
| 6 | unexpected signal | a node logged a failure signal that no injected fault explains, or exited by itself |

When several apply, the first in the order 1, 3, 4, 6, 5 wins.

### Artifacts

`<out>/report.json` (schema version 1): command, seed, schedule, bounds, node settings, environment (OS, CPU,
filesystem, runtime, revision), per-episode results (victims, leader and term before and after, recovery time,
commit index at the checkpoint, snapshots installed; for a partition, the peers cut, the majority leader and term,
the writes acknowledged during the partition, the writes sent to the isolated node by outcome, when the isolated
leader stepped down, and how long the partition lasted), proxy statistics (`proxy`), per-node incarnations and
unexpected exits, classified log signals with examples, and the verdict. `<out>/history.json`: every node's applied history at the end (key, index,
term) and every acknowledged write; one that no checkpoint audited, because the run stopped first, has no `index`.
`<out>/logs/node<i>.<incarnation>.log`: one JSON log line per event.
`<out>/claims/node<i>.log`: the terms in which each node became leader, across incarnations. `<out>/data`: the WAL and
state machine directories, deleted after a passing run unless `--keep-data true`.

## Shape

`run` is the driver; it starts each node as `sh -c 'exec <tool> node ... > log 2>&1'`, so the process the driver
signals is the node itself and its log survives the driver.

**Process under test** (`Node/NodeHost.cs`). One node, hosted like `src/examples/RaftNode`: `JoinCluster` on a slim
`WebApplication` over HTTP, or `RaftCluster.TcpConfiguration` over TCP; static in-memory membership of three voters;
`WriteAheadLog` with its library defaults (shared memory, `FlushInterval = 0`). The state machine is the stage 2
`HistoryStateMachine`, whose snapshot carries the whole applied history, so the oracles hold through compaction and
`InstallSnapshot`. Deviations from RaftNode, all deliberate:

- election timeout 1000-2000 ms, as in the stage 2 tool, instead of 150-300 ms: every append is persisted before it
  completes, and a short election timeout churns leadership on a slow runner disk without any fault;
- request timeout 3 s over HTTP, as in the stage 2 tool (vote requests use HTTP's `RpcTimeout`, by default 1 s). Over
  TCP it is the library default, the lower election timeout (1 s): over TCP the request timeout also bounds vote
  requests, and a vote round waits for every member, so with 3 s a silent member stalls every election of the majority
  ([#146](https://github.com/damianh/dotNext/issues/146), found by the partition episodes);
- the node listens on a private port and advertises the port of its proxy in the driver (`publicEndPoint` over HTTP,
  `TcpConfiguration.PublicEndPoint` over TCP), and the member list holds the proxy ports (see "Partitions");
- a separate loopback control port with `POST /write` (`ReplicateAsync`, 10 s timeout; 200 acknowledged, 409 rejected
  because not appended, 503 unknown), `GET /status` and `GET /history` (paged applied history);
- JSON console logging, so the driver can classify events by id;
- each leader election is appended to a claims file outside the data directory, which survives SIGKILL;
- no peer authentication; it listens on loopback only.

**Workload.** Four closed-loop clients send unique writes to the current leader. A write whose outcome is unknown (the
node died, or leadership was lost) is never retried with the same key, so a duplicate is always a bug.

**Schedule.** Warmup to index 150, then ten episodes:

| Episode | Fault |
|---|---|
| `leader-kill` (x3) | SIGKILL the leader, hold it down 0.5-4 s, restart it |
| `leader-term` | SIGTERM the leader (graceful stop; 15 s grace, else it is killed and reported), hold, restart |
| `follower-kill` | SIGKILL a follower, hold, restart |
| `lagging-snapshot` | SIGKILL a follower, keep it down until both other nodes have a snapshot past its log, restart: it must catch up by `InstallSnapshot` (no install is exit 5) |
| `cluster-kill` | SIGKILL all three at once, hold, restart all: everything comes from disk |
| `leader-partition` | cut the leader from both followers; wait until the majority elects a leader in a new term and acknowledges 20 writes, hold, heal |
| `follower-partition` | cut a follower from the leader and the other follower under load; wait for 20 majority acknowledgments, hold, heal |
| `partition-mid-election` | cut the leader and heal after 1-3 s, whatever the majority has done: the heal usually lands while it is still electing |

The partition episodes come after the kill episodes, so a seed keeps the victims and hold times of its first seven
episodes from before they were added.

**Partitions** (`Driver/PartitionNetwork.cs`). Each node listens on a private port; the driver runs one userspace TCP
proxy per node on the port the node advertises, and forwards to the private port. Every Raft connection to node `d`,
from any peer, therefore passes through the proxy of `d`. The proxy finds which node opened a connection from
`/proc/net/tcp` (the inode of the client socket) and `/proc/<pid>/fd` (the node process that owns it); the nodes run as
the same user, so no privileges are needed. Isolating node `n` cuts the links `n`-`p` for both peers `p`: the proxy of
`n` drops the bytes of connections from `p`, and the proxy of `p` drops those of connections from `n`, in both
directions. Connections stay open, so a request across the cut times out as it does when a real network splits, and a
close or reset doesn't cross the cut either; new connections are accepted and dropped the same way. A connection whose source cannot be attributed is treated as cut
while any link is cut (`proxy.unattributedConnections` in the report; 0-1 per run so far). Healing restores every
link and resets the connections that dropped bytes or a close, since their streams lost data. Client writes and status polls go to
the control port and are not proxied. Nothing outside the campaign's own ports changes: no `iptables`, `tc` or network
namespaces.
**Recovery.** After the restart or the heal, within `--recovery-timeout`: every node answers `/status`, a leader
exists and the clients get 20 new acknowledgments; after a leader partition, the old leader no longer leads in its old
term. A leader or follower partition also needs the majority side to elect a leader (in a new term, if the leader was
cut) and acknowledge 20 writes within `--recovery-timeout` of the cut, else exit 4.

**Oracles** (the stage 2 `OnlineHistoryChecker` and `SimulationHistory`), after warmup and after each episode. The
checkpoint waits for the acknowledgment of a write submitted after every acknowledgment being checked, then for every
node to apply up to the highest commit index reported; any write acknowledged before the checkpoint is at or below that
index. At the end the clients stop after their requests in flight, one more write is acknowledged, and a final
checkpoint audits every acknowledged write, so `history.json` of a passing run holds all of them. Then:

- apply order: per node, contiguous indices and no duplicate writes, across restarts and snapshot installs;
- committed-prefix agreement: every node's applied history is a prefix of one sequence;
- acknowledged writes: each acknowledged write is at the same index on every node that applied that index;
- recovery (durability): each acknowledged write is in every node's history, at its index;
- election safety: at most one leader claim per term, across all incarnations, including the terms of an isolated
  node and of the majority during a partition.

**Partition oracles** (`Driver/PartitionOracle.cs`). While a node is cut, a probe client keeps writing to it (it
retries every 20 ms, so it inflates the rejected count of `workload`) and the four clients write to whichever node
reports that it leads. Every write sent to the isolated node is recorded with its outcome (`minorityWrites` in the
episode report); the cut and the heal happen under the oracle's lock, so a write is recorded if and only if it is
submitted while the node is cut. In a leader or follower partition the majority elects a leader in a higher term, if needed, and
commits in it before the heal, so the isolated node can never commit a write it received while cut: its uncommitted
entries must be discarded. Hence:

- minority acknowledgment: the isolated node acknowledges no write it received while cut;
- minority write applied: no node, at any later checkpoint, has applied such a write.

A violation is exit 3. The writes acknowledged during the partition all come from the majority side, and the existing
oracles require every one of them on every node after the heal. A `partition-mid-election` heals before the majority has
necessarily committed in a new term, so the old leader may still legitimately commit what it appended while cut: there
the writes sent to it are counted, not judged, and the other oracles apply as usual.

**Failure signals** (`Driver/LogClassifier.cs`), classified by event id; the unexpected ones map to RAFT-REVIEW "Failure
signals and operator actions (#26)". Expected, accepted at any time because they are transient and retried (a persistent
one fails the liveness oracles, exit 4): Warning 74010 `MemberUnavailable` and HTTP 75001 `MemberUnavailable` (a peer is
down, restarting or being terminated), 74015 `ReplicationFailed` (log mismatch after an election, including warmup), and
EventId 0 request failures. Partitions add no expected signal: while a node is cut, its peers and the node itself log
74010 or 75001 for the requests that time out across the cut, and the leader on the majority side logs Warning 74044
`SlowMember` ("too far behind the leader") for the cut follower while its replication queue is full. 74044 is not in the
#26 table, so it is reported as unclassified and does not fail the run. Unexpected (exit 6): 74028, 74030, 74031, 74032, 74035, 74037, 74048,
74049, HTTP 75002, any Critical, `IntegrityException`, `HashMismatchException`, `MissingPageException`, a terminal WAL
failure, an unhandled exception, a node exit the driver did not cause, or a failed background snapshot reported in a node's
status (`snapshotFailures` in `report.json`). A write that fails with an unexpected exception on a live node is logged as
Critical; during a SIGTERM stop it is a Warning. Any other Warning or Error is reported as
unclassified and does not fail the run.

## Oracle teeth

```bash
# One node skips the apply of index 100 in its first incarnation: apply order, exit 3.
./bin/Release/net10.0/DotNext.Raft.FaultCampaign run --transport http --inject drop-applied --episodes leader-kill --out /tmp/fc/drop
# Every node starts from an empty data directory on every launch: a cluster kill loses acknowledged writes, exit 3.
./bin/Release/net10.0/DotNext.Raft.FaultCampaign run --transport tcp --inject volatile-storage --episodes cluster-kill --out /tmp/fc/vol
# The proxy leaves one link of the isolated leader up, so it keeps a majority and acknowledges writes: minority acknowledgment, exit 3.
./bin/Release/net10.0/DotNext.Raft.FaultCampaign run --transport http --inject partition-leak --episodes leader-partition --out /tmp/fc/leak
```

CI (`.github/workflows/raft-fault-campaign.yml`) runs the three injections on both transports and requires exit 3 with
a named oracle. `FaultCampaignHarnessTests` in `DotNext.Tests` covers the history feed, the recovery audit, the signal
classifier, the schedule, the command line, the partition proxy (link mask, `/proc/net/tcp` parsing, and a loopback
connection whose bytes or close are dropped while cut and reset at heal) and the partition oracle.

## Bounds

Loopback only; no privileges, no OS-wide network or disk changes, no cache dropping. Partitions are made by the
driver's own proxies on the campaign's ports. The run stops at
`--max-duration`. It needs 2 GiB free in `--out` to start and stops below 1 GiB. Nodes are stopped when the driver
exits normally; if the driver itself is killed, its nodes may outlive it (CI runners clean up at the end of the job).
