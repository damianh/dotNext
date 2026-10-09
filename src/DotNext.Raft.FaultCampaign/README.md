# Raft fault campaign (#118 stage 3)

A real-process fault-injection smoke campaign for the fork's Raft implementation. A 3-node cluster runs as three
separate OS processes on loopback, over the HTTP or the TCP transport. The campaign kills and restarts nodes under a
closed-loop write load and checks safety, durability and liveness oracles after each fault. Background, results and
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

A run takes about 45 seconds. The seed picks the victim followers and the hold times; the order of faults is fixed, so
every run covers every fault. Re-running with the same seed and transport repeats the same schedule, though not the
same interleaving (real processes and real time).

| Option | Default | |
|---|---|---|
| `--transport http\|tcp` | `http` | the Raft transport |
| `--out <dir>` | `./fault-campaign` | the artifact directory; its `logs`, `data` and `claims` subdirectories are recreated |
| `--seed <n>` | 1 | victims and hold times |
| `--episodes <list>` | all | a comma-separated subset of the schedule, in schedule order |
| `--inject none\|drop-applied\|volatile-storage` | `none` | a test-only failure the oracles must catch |
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
commit index at the checkpoint, snapshots installed), per-node incarnations and unexpected exits, classified log
signals with examples, and the verdict. `<out>/history.json`: every node's applied history at the end (key, index,
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

- election timeout 1000-2000 ms and request timeout 3 s, as in the stage 2 tool, instead of 150-300 ms: every append is
  persisted before it completes, and a short election timeout churns leadership on a slow runner disk without any fault;
- a separate loopback control port with `POST /write` (`ReplicateAsync`, 10 s timeout; 200 acknowledged, 409 rejected
  because not appended, 503 unknown), `GET /status` and `GET /history` (paged applied history);
- JSON console logging, so the driver can classify events by id;
- each leader election is appended to a claims file outside the data directory, which survives SIGKILL;
- no peer authentication; it listens on loopback only.

**Workload.** Four closed-loop clients send unique writes to the current leader. A write whose outcome is unknown (the
node died, or leadership was lost) is never retried with the same key, so a duplicate is always a bug.

**Schedule.** Warmup to index 150, then seven episodes:

| Episode | Fault |
|---|---|
| `leader-kill` (x3) | SIGKILL the leader, hold it down 0.5-4 s, restart it |
| `leader-term` | SIGTERM the leader (graceful stop; 15 s grace, else it is killed and reported), hold, restart |
| `follower-kill` | SIGKILL a follower, hold, restart |
| `lagging-snapshot` | SIGKILL a follower, keep it down until both other nodes have a snapshot past its log, restart: it must catch up by `InstallSnapshot` (no install is exit 5) |
| `cluster-kill` | SIGKILL all three at once, hold, restart all: everything comes from disk |

**Recovery.** After the restart, within `--recovery-timeout`: every node answers `/status`, a leader exists and the
clients get 20 new acknowledgments.

**Oracles** (the stage 2 `OnlineHistoryChecker` and `SimulationHistory`), after warmup and after each episode. The
checkpoint waits for the acknowledgment of a write submitted after every acknowledgment being checked, then for every
node to apply up to the highest commit index reported; any write acknowledged before the checkpoint is at or below that
index. At the end the clients stop after their requests in flight, one more write is acknowledged, and a final
checkpoint audits every acknowledged write, so `history.json` of a passing run holds all of them. Then:

- apply order: per node, contiguous indices and no duplicate writes, across restarts and snapshot installs;
- committed-prefix agreement: every node's applied history is a prefix of one sequence;
- acknowledged writes: each acknowledged write is at the same index on every node that applied that index;
- recovery (durability): each acknowledged write is in every node's history, at its index;
- election safety: at most one leader claim per term, across all incarnations.

**Failure signals** (`Driver/LogClassifier.cs`), classified by event id; the unexpected ones map to RAFT-REVIEW "Failure
signals and operator actions (#26)". Expected, accepted at any time because they are transient and retried (a persistent
one fails the liveness oracles, exit 4): Warning 74010 `MemberUnavailable` and HTTP 75001 `MemberUnavailable` (a peer is
down, restarting or being terminated), 74015 `ReplicationFailed` (log mismatch after an election, including warmup), and
EventId 0 request failures. Unexpected (exit 6): 74028, 74030, 74031, 74032, 74035, 74037, 74048,
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
```

CI (`.github/workflows/raft-fault-campaign.yml`) runs both injections on both transports and requires exit 3 with a
named oracle. `FaultCampaignHarnessTests` in `DotNext.Tests` covers the history feed, the recovery audit, the signal
classifier, the schedule and the command line.

## Bounds

Loopback only; no privileges, no OS-wide network or disk changes, no cache dropping. The run stops at
`--max-duration`. It needs 2 GiB free in `--out` to start and stops below 1 GiB. Nodes are stopped when the driver
exits normally; if the driver itself is killed, its nodes may outlive it (CI runners clean up at the end of the job).
