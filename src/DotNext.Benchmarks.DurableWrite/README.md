# Durable-write load baselines (`DotNext.Benchmarks.DurableWrite`)

A bounded, reproducible load tool for the durable write path of the fork: `WriteAheadLog` on its own, and
`RaftCluster` with 1, 3 and 5 voters over real loopback TCP. It records baselines (throughput, latency
histograms, flush and apply cost, lag, backlog, CPU/GC and write amplification) as JSON with the environment
metadata, and it fails **only** on correctness oracles. There are no latency, throughput or memory thresholds.

This is stage 2 of #118. Real-process fault and burn-in campaigns (process kill, power loss, HTTP/UDP transports)
are stage 3; see [Blind spots](#blind-spots).

The older [`DotNext.Benchmarks.WAL`](../DotNext.Benchmarks.WAL/Program.cs) (2,000 x 1 KiB appends into one
`WriteAheadLog`, compared against FASTER) is unchanged; it compares raw log implementations and carries the FASTER
dependency. This tool does not replace it.

## Running

```powershell
# about 2 minutes: the CI smoke matrix
dotnet run -c Release --project src\DotNext.Benchmarks.DurableWrite -- --profile smoke --out smoke.json

# about 15 minutes: the baseline matrix
dotnet run -c Release --project src\DotNext.Benchmarks.DurableWrite -- --profile full --out full.json

# each must exit with code 3 and name the oracle that caught the injected failure
dotnet run -c Release --project src\DotNext.Benchmarks.DurableWrite -- --inject drop-applied
dotnet run -c Release --project src\DotNext.Benchmarks.DurableWrite -- --inject reorder-applied
dotnet run -c Release --project src\DotNext.Benchmarks.DurableWrite -- --inject non-durable-followers
```

Run `--help` for every option. Useful ones: `--mode wal|raft`, `--voters 3`, `--sizes 128,16384`,
`--concurrency 1,16`, `--duration <s>`, `--memory shared|private`, `--work-dir <dir>`, `--keep-data`,
`--max-duration <minutes>`. `--voters`, `--sizes` and `--concurrency` also shape the slow-follower cells: they run on
the given cluster sizes of three or more, with the smallest entry size and the highest client count. `--duration`
also sets the slow-follower window; it must be long enough for the follower to fall behind a snapshot (see below).

Exit codes: `0` every oracle passed, `1` unexpected error, `2` usage, `3` safety oracle violation, `4` liveness
failure (no leader within 30 s at startup or after the workload, a node did not catch up within the bound, or the slow
follower installed no snapshot), `5`
incomplete (`--max-duration` was reached before every cell ran its final checks, or a cell was skipped for lack of
disk space; the report then has `incomplete` set). On a non-zero exit the node data is kept for diagnosis.

CI (`.github/workflows/durable-write-load.yml`):

- **smoke**, on pull requests and pushes to `fork` that touch the WAL, Raft or the tool: the smoke profile, then the
  three injections, each of which must exit 3 with the expected oracle. 15-minute job timeout.
- **baseline**, `workflow_dispatch` only, with inputs `os`, `profile` and `memory`: uploads the JSON report and the
  console log as an artifact, and writes a summary table to the step summary. 60-minute job timeout. The optional
  inputs `diagnostics`, `cells` and `repeat` pass `--diagnostics`, `--cells` and `--repeat` and add the breakdown
  and spread tables to the summary; with `diagnostics` on Linux, a further step counts the durability system calls
  per acknowledged write with `strace -c` (see [Diagnostics](#diagnostics---diagnostics---repeat---cells-123)).

When either job fails, the node data kept under the runner's temp directory is uploaded as a separate artifact
(5-day retention).

## What is measured

### Modes and cells

| Kind | What it does |
|---|---|
| `wal-append` | C concurrent writers on one `WriteAheadLog`; each does `AppendAsync`, `CommitAsync`, then waits for the apply. The ack latency is that whole round trip. |
| `wal-batch` | One writer appends batches of B entries at an explicit index (`AppendAsync(ILogEntryProducer, startIndex)`, the follower's group-commit path), then commits and waits for the apply. |
| `raft-closed` | V voters over loopback TCP, C closed-loop clients calling `RaftCluster.ReplicateAsync` on the leader. |
| `raft-open` | Writes offered at a fixed rate, a fraction of the throughput of the busiest closed-loop cell measured in the same run. The latency is measured from the intended send time, so queueing shows up in it (no coordinated omission). In-flight writes are capped at 4,096; an offer above the cap counts as `overloaded`. |
| `slow-follower` | V voters, frequent snapshots, and one follower behind a TCP relay that delays every chunk by 5 ms and pauses for 5 s at 30% of the run. The follower falls behind the snapshot index and must catch up through `InstallSnapshot` while the load continues. If it installs no snapshot, the cell did not test what it is for, and the run exits 4. |

| Profile | Matrix | Duration per cell |
|---|---|---|
| `smoke` | wal: {128 B, 16 KiB} x {C=8, B=64}; raft: 1 and 3 voters at 128 B, C=8, 3 voters at 16 KiB, C=4; open loop at 50%; one 3-voter slow follower at C=8. Snapshot every 1,000 entries (slow follower: 200). 9 cells. | warmup + measured: 0.5 s + 2 s (wal), 1 s + 4 s (raft closed), 1 s + 3 s (raft open), 10 s (slow follower) |
| `full` | wal: {128 B, 16 KiB} x ({C=1, 16, 64} + {B=16, 256} + C=16 on the other memory strategy + C=16 private with `NoBuffering`); raft: {1, 3, 5} voters x {128 B, 16 KiB} x {C=1, 16, 64}; 3-voter open loop at {50, 90, 120}%; slow follower with 3 and 5 voters at C=16. Snapshot every 5,000 entries (slow follower: 500). 37 cells. | 2 s + 10 s (wal), 5 s + 20 s (raft), 30 s (slow follower) |

**Bounds.** Every cell also stops at `--max-entries` (default 200,000 acknowledged writes) or `--max-payload-gib`
(default 2 GiB); the whole run stops at `--max-duration` (default 30 minutes) and exits 5. A cell is skipped (exit 5)
when the volume that holds the work directory (the longest matching mount point) has less than 2 GiB free, and
stopped when free space drops below 1 GiB. Node data goes under `--work-dir` (default `%TEMP%/dotnext-durable-write`) and is
deleted after each cell. Only loopback ports chosen by the OS are used. The tool drops no caches and needs no
privileges.

**Batching.** Raft's leader appends one entry per `ReplicateAsync`; no batching of client proposals is exposed
through the public API. Followers receive multi-entry AppendEntries under concurrency, which is the path the
`wal-batch` cells measure on their own.

**Transport.** All nodes of a cell run in one process and talk over real loopback TCP through the public API
(`RaftCluster.TcpConfiguration`, static membership). This measures socket I/O, framing and serialization, the
real disk I/O of every node and the leader's wait for commit and apply. It does not measure cross-process
isolation (the nodes share CPU, GC and thread pool, so CPU and GC are per process), the HTTP transport, a real
network, or process kill. The in-process test transport was not used: it needs library internals that are visible
only to Debug test builds, and it bypasses serialization and sockets.

### Metrics (per cell, in the JSON)

- `offered`, `completed`, `rejected` (`NotLeaderException`, nothing written), `unknown` (leadership lost after the
  append) and `overloaded` counts in the measured window; offered and completed per second; payload MiB/s;
  `acknowledgedTotal` (warmup included, every one of them is checked by the oracles); leader changes;
- `ackLatency`: p50, p90, p99, p99.9 and max of the client acknowledgment, from a log-bucketed histogram;
- `appendLatency` and `commitApplyLatency` (wal mode): the durable append, and the commit-to-apply step;
- `checkpointFlushDuration` and `applyDuration`, from the WAL meter (`entries-flush-duration`, `entries-apply-duration`);
- `replicaApplyLag`: for each index, the time from the first node applying it to each other node applying it;
- `backlog`: sampled every 10 ms; the max of uncommitted entries on the leader, committed but unapplied entries,
  the spread of applied indexes across nodes (follower lag) and in-flight client writes;
- `process`: CPU seconds and cores used, GC counts per generation, GC pause time, allocated bytes, working set;
- `writeAmplification`: payload bytes vs bytes appended by the leader and by the cluster (`entries-append-bytes`),
  and vs the length of every file under the node directories at the end (`clusterFileRatio`; this includes
  preallocated chunk space, snapshots and checkpoints, so it is high for short cells with small entries);
- per node: appended, flushed, committed and applied counts, `flushCoverage` (flushes per append, information
  only: the WAL meter counts the background flusher's flushes too, so it cannot prove that an append was flushed),
  snapshots taken and installed, entries recovered from disk after the run, and bytes through the relay;
- `noBuffering`: whether the cell's write-ahead logs used unbuffered I/O.

The run carries `environment` (revision, OS, CPU model and count, runtime, GC mode, filesystem and device type of the
work directory, free space), `durability` (the WAL settings below), `bounds`, `fsyncProbe` and `incomplete` (why the
run stopped early, or `null`).

The JSON `schemaVersion` is 3. Version 3 (#123) only adds optional fields: `syncProbe`, `repeats`,
`cells[].repeat` and `cells[].diagnostics`. They are `null` unless `--diagnostics` or `--repeat` is given, so a
default run carries the same data as version 2. Version 1, used by the committed baseline report below, differs in
that: it has no `incomplete`, `cells[].noBuffering`, `nodes[].flushCoverage` or `oracles.reconciledEntries`;
`durability.noBuffering` was a boolean (now a description, since it varies per cell); and it listed flush coverage
among the checked oracles.

### Diagnostics (`--diagnostics`, `--repeat`, `--cells`, #123)

These options are for investigating where the time goes. They change no cell's workload, settings or oracles. Every
oracle still runs, and the exit codes are the same.

```powershell
# persist cycle, lock and Raft breakdowns for the 3-voter closed-loop cells of the full matrix
dotnet run -c Release --project src\DotNext.Benchmarks.DurableWrite -- --profile full --mode raft --voters 3 --diagnostics --out diag.json

# the same cells five times on one host, to measure run-to-run spread
dotnet run -c Release --project src\DotNext.Benchmarks.DurableWrite -- --profile full --cells raft-closed-3v-128B --repeat 5 --out spread.json
```

- `--cells <text>[,<text>...]` keeps only the cells whose name contains one of the texts as whole dash-separated
  parts: `3v-128B` selects every 3-voter 128-byte cell, and `wal-append-128B-c1` does not select
  `wal-append-128B-c16-private`. A selected open-loop cell always brings along all the closed-loop cells with its voters
  and entry size, because its offered rate is a fraction of the busiest of them. A selection that matches no cell is a
  usage error (exit 2).
- `--repeat <n>` (1 to 20) runs the selected matrix n times, round by round, and names each round's cells with a
  suffix `-r1` to `-rn`. `repeats[]` in the JSON gives, for each cell, the min, median, max, mean, coefficient of
  variation and range (as a percentage of the median) of the throughput, the ack p50 and p99, and the leader
  changes. The console prints a `spread` line per cell.
- `--diagnostics` subscribes to the opt-in WAL instruments and the Raft meters, and adds `cells[].diagnostics`:
  - `persistCycles[]`: per node and cause (`append` is the persist inside `AppendAsync`; `flush` is the background
    flusher after a commit or apply). Each has the cycles, cycles per acknowledged write, entries per cycle (the
    group-commit factor; `append` cycles only, null for `flush` because an append cycle also checkpoints the commit
    index, so the committed counter cannot be split by cause), total ms and ms per ack, plus a duration summary per
    phase: `pages` (write and flush of the
    dirty pages), `data-directory` and `metadata-directory` (`FlushDirectory`), and the three checkpoint steps
    `checkpoint-intent`, `checkpoint-slot`, `checkpoint-commit`.
  - `locks[]`: per node, lock and cause, the wait (from the acquire call to the grant) and the hold (from the grant to
    the release). The hold is reported for the `persistence` lock only; the WAL read and write locks record the wait.
  - `raft`: per node, `broadcast-time`, the gap between broadcast rounds, the `heartbeatGap` and the state
    transitions; `response-time` per message type and remote node; the late-response count; and the maximum
    broadcast and heartbeat gaps of the cell against the election timeout. The heartbeat gap is the time between two
    resets of the follower's election timer (`incoming-heartbeats-count`): a leader message resets it, and so does a
    vote granted in the current term, so it is a lower bound on the time between leader messages and exactly the
    interval the election timer sees.
  - `events[]` (at most 500, `eventsDropped` counts the rest): leader claims with the term, and state transitions,
    each with the time since the measured window started.
  - `seconds[]`: one row per second of the measured window, with acks, the maximum uncommitted backlog, the longest
    broadcast and heartbeat gap, the longest commit-lock and persistence-lock wait, and the transitions. This is
    where a stall lines up against a term change.
  - `nodes[]`: the appended, committed and flushed counter deltas in the measured window.
  - `io`: process I/O counters for the measured window (Windows `GetProcessIoCounters`: the `otherOps` count includes
    flushes, but also other control operations, so it is an upper bound on the flushes; Linux `/proc/self/io` and, for
    the device under the work directory, the write and flush counts from `/proc/diskstats`, which are system-wide).
  - At run level, `syncProbe` measures on the same device a directory flush, a publish (write, flush, rename,
    directory flush: what a checkpoint intent costs) and a delete with a directory flush (a checkpoint commit).

  The instruments are on the `DotNext.IO.WriteAheadLog` meter (`persist-phase-duration`, `lock-wait-duration`,
  `lock-hold-duration`) and in the `DotNext-IO-WriteAheadLog` EventSource (keyword `0x1`). Both cost nothing unless a
  listener subscribes, and the tool subscribes only with `--diagnostics`. An external process can read the same
  events without the tool's help:

  ```bash
  dotnet-trace collect -p <pid> --providers DotNext-IO-WriteAheadLog:0x1:4
  dotnet-counters monitor -p <pid> --counters DotNext.IO.WriteAheadLog
  ```

  On Linux, the durability system calls per write can be counted without privileges by tracing the tool itself:

  ```bash
  strace -f -c -e trace=fsync,fdatasync,msync,sync_file_range,rename,renameat,renameat2,unlinkat \
    dotnet src/DotNext.Benchmarks.DurableWrite/bin/Release/net10.0/DotNext.Benchmarks.DurableWrite.dll \
    --mode wal --cells wal-append-128B-c1 --profile full --concurrency 1
  ```

  The diagnostics listener only records a few timestamps per persist cycle (1.6 to 5 ms long); on Linux CI the
  closed-loop throughput with and without `--diagnostics` differed by less than 4%, within the run-to-run spread.

  The findings of the #123 investigation (the cost of one persist cycle, the absence of group commit, the lock queue
  behind elections at overload, and the run-to-run spread) are in
  [RAFT-REVIEW.md](../../RAFT-REVIEW.md#durable-write-latency-investigation-123).

## Durability

Every node uses a `WriteAheadLog` with:

| Option | Value | Why |
|---|---|---|
| `FlushInterval` | `TimeSpan.Zero` (the default) | a commit checkpoint on every commit |
| `MemoryManagement` | `SharedMemory` (the default), `--memory private` to switch | |
| `NoBuffering` | `false`; one `full` cell per size uses `PrivateMemory` + `NoBuffering` | reported per cell |
| `ChunkSize` | 4 MiB | the default (one system page) is too small for 16 KiB entries |
| `HashAlgorithm` | default (none) | |

What makes the acknowledgment durable on the fork, checked in the code:

- Every append ends in `WriteAheadLog.PersistAppendAsync`. Under the append lock, it flushes the data and metadata
  pages to the device, flushes both directories and writes the recovery checkpoint. Only then does it publish
  `LastEntryIndex`. This is publish-after-durable, the same rule as #24 for term and vote. `FlushInterval` covers
  only the commit checkpoint, not entry durability.
- A follower replies to AppendEntries only after that persist, and the leader counts a replica only on the reply.
- `RaftCluster.ReplicateAsync` appends durably on the leader and replicates to a majority. It returns once the
  entry is committed and applied on the leader. So an acknowledged write is on the disk of a majority.

How the tool shows that it is not measuring buffered writes:

1. **Durability oracle (recovery audit).** After the workload, each node is stopped and disposed. A fresh
   `WriteAheadLog` and state machine are opened on its directory, and the recovered history is read back from the
   snapshot, the replayed committed entries and the uncommitted tail. Every acknowledged write must be recovered at
   its index, with its payload, on a majority of voters. The `non-durable-followers` injection shows that this
   catches acknowledgments that were never written (see [Oracle teeth](#oracle-teeth)).
2. **History reconciliation.** Each node's recovered log must also hold every entry its own state machine applied,
   with the same write and term at the same index (see [Oracles](#oracles)).
3. **Device calibration (information only).** Before the cells, `fsyncProbe` times a 4 KiB write with
   `FileStream.Flush(true)` and a buffered write on the work volume. When the two are distinguishable, a cell whose
   durable latency p50 (the append latency in wal cells, the ack latency in raft cells) is below half the fsync p50 is
   marked `suspectBuffered`. This is a warning, not a failure: devices with power-loss-protected caches legitimately
   flush in microseconds.

The recovery audit runs in the same OS instance. It proves that the data reached the file system, not that it
survives a power loss or a lost page cache; see [Blind spots](#blind-spots).

## Oracles

The history is checked **while the workload runs**. Each node's `HistoryStateMachine` reports every applied entry to
`OnlineHistoryChecker`, and the first violation stops the cell.

| Oracle | Checked | Catches |
|---|---|---|
| apply order | on every apply and snapshot install | an index applied out of sequence or skipped while another node applied a write there; a write applied at two indexes; a closed-loop client's writes applied out of the order it sent them |
| committed-prefix agreement | on every apply, and on all final prefixes through `SimulationHistory`; leader no-ops (never applied) get their terms read back from each node's log at the end, but a no-op already compacted into a snapshot on every node cannot be compared | two nodes applying different terms or writes at one index |
| acknowledged writes | on every acknowledgment, and at the end through `SimulationHistory` | an acknowledged write that no node has applied, or that is missing from or replaced in the final prefixes |
| election safety | on every leader change, through `SimulationHistory` | two leaders in one term |
| durability | after the cell | an acknowledged write that a majority does not recover from disk |
| history reconciliation | after the cell, per node | a write the node's log applied past the end of its reported history (a silently lost last apply, which no later callback exposes); an applied index that cannot be read back; a recovered log that is shorter than the history or holds a different write or term at an applied index, or a write where the state machine applied none |

`oracles.reconciledEntries` counts the applied history entries compared by the reconciliation, summed over nodes. The
flush/append ratio per node (`flushCoverage`) is not an oracle: the WAL meter does not separate the append path's
flushes from the background flusher's.

`SimulationHistory` is the oracle class of the seeded simulation (#56 stage 1), source-linked from DotNext.Tests.
The state machine snapshot holds the full applied history, so a node restored from a snapshot still has a gapless
history from index 1. That keeps the oracles working through compaction, which stage 1 could not cover.

### Oracle teeth

`--inject` runs one short 3-voter cell with a known failure. Each mode is test-only code in this tool; nothing in
the library changes.

| Injection | Failure | Caught by |
|---|---|---|
| `drop-applied` | A follower's state machine silently drops the entry at index 100 | apply order: `node 1 skipped index 100, which node 0 applied as 'm0-c4-s13'` |
| `reorder-applied` | A follower's state machine holds back the entry at index 100 and applies it after the next one | committed-prefix agreement: `index 100 is applied as (term 1, 'm0-c6-s13') on node 2 but as (term 1, 'm0-c2-s13') on node 0` |
| `non-durable-followers` | Followers use the in-memory `ConsensusOnlyState`: they acknowledge without writing anything (the "skip the flush before the ack" case) | durability: `'m0-c7-s1' was acknowledged by node 0 at index 5, but only 1 of 3 voters recovered it from storage; a majority is 2` |

`DurableWriteOracleTests` in DotNext.Tests checks the same oracles against synthetic histories:

```powershell
dotnet run --project src\DotNext.Tests\DotNext.Tests.csproj -- --filter-class '*DurableWriteOracleTests' --progress off
```

## Baselines

These numbers are **baselines, not thresholds**. Nothing in CI compares against them; a run fails only on an oracle
violation. Use them to see whether a change moves throughput or latency on the same host.

### Windows, i9-14900K, NTFS (2026-10-05, revision 3dd6faf5)

Raw report: [`baselines/windows-i9-14900K-2026-10-05.json`](baselines/windows-i9-14900K-2026-10-05.json), profile
`full`, shared memory, every other option at its default.

- Intel Core i9-14900K (32 logical processors), 128 GiB RAM, Windows 10.0.26300 x64, NTFS on a local fixed disk.
- .NET 10.0.12, workstation concurrent GC.
- `fsyncProbe`: a flushed 4 KiB write takes p50 497 µs and p99 987 µs; a buffered one p50 5 µs (distinguishable).
- Every oracle passed in every cell, and no cell was `suspectBuffered`. (The report predates schema version 2: it ran
  the flush coverage check, since demoted to information, and not yet the history reconciliation.) The highest CPU use in any cell was 0.47
  cores, the longest GC pause total 10 ms: every cell is I/O-bound.

`done/s` counts acknowledged entries (in `wal-batch` cells, entries, not batches; the latency is per batch).
`run 1` is an earlier full run on the same host and revision, to show run-to-run spread. Its open-loop cells used
an older reference rate (the c64 cell), so they are not comparable.

| Cell | done/s | ack p50 ms | p99 ms | p99.9 ms | run 1 done/s |
|---|---:|---:|---:|---:|---:|
| `wal-append-128B-c1` | 70 | 10 | 82 | 91 | 74 |
| `wal-append-128B-c16` | 137 | 93 | 280 | 299 | 141 |
| `wal-append-128B-c64` | 189 | 306 | 610 | 1128 | 137 |
| `wal-batch-128B-b16` | 1660 | 9.3 | 14 | 17 | 1026 |
| `wal-batch-128B-b256` | 16441 | 12 | 84 | 91 | 12338 |
| `wal-append-128B-c16-private` | 209 | 70 | 154 | 183 | 201 |
| `wal-append-128B-c16-private-nobuffering` | 174 | 77 | 224 | 295 | 108 |
| `wal-append-16KiB-c1` | 79 | 10 | 82 | 118 | 101 |
| `wal-append-16KiB-c16` | 180 | 74 | 344 | 1136 | 89 |
| `wal-append-16KiB-c64` | 94 | 335 | 1266 | 2514 | 189 |
| `wal-batch-16KiB-b16` | 400 | 39 | 55 | 64 | 1289 |
| `wal-batch-16KiB-b256` | 4090 | 58 | 187 | 212 | 9572 |
| `wal-append-16KiB-c16-private` | 214 | 70 | 152 | 164 | 188 |
| `wal-append-16KiB-c16-private-nobuffering` | 189 | 77 | 166 | 253 | 198 |
| `raft-closed-1v-128B-c1` | 100 | 9.5 | 19 | 49 | 76 |
| `raft-closed-1v-128B-c16` | 95 | 81 | 588 | 633 | 45 |
| `raft-closed-1v-128B-c64` | 47 | 1253 | 2507 | 3589 | 54 |
| `raft-closed-1v-16KiB-c1` | 24 | 38 | 70 | 79 | 25 |
| `raft-closed-1v-16KiB-c16` | 49 | 308 | 653 | 687 | 51 |
| `raft-closed-1v-16KiB-c64` | 47 | 1241 | 2466 | 2819 | 50 |
| `raft-closed-3v-128B-c1` | 51 | 18 | 74 | 94 | 40 |
| `raft-closed-3v-128B-c16` | 167 | 92 | 169 | 191 | 154 |
| `raft-closed-3v-128B-c64` | 189 | 340 | 625 | 698 | 92 |
| `raft-closed-3v-16KiB-c1` | 58 | 17 | 23 | 29 | 6 |
| `raft-closed-3v-16KiB-c16` | 168 | 90 | 160 | 206 | 167 |
| `raft-closed-3v-16KiB-c64` | 202 | 312 | 594 | 651 | 167 |
| `raft-closed-5v-128B-c1` | 49 | 20 | 26 | 28 | 47 |
| `raft-closed-5v-128B-c16` | 149 | 103 | 177 | 199 | 144 |
| `raft-closed-5v-128B-c64` | 184 | 344 | 602 | 690 | 186 |
| `raft-closed-5v-16KiB-c1` | 48 | 20 | 39 | 128 | 49 |
| `raft-closed-5v-16KiB-c16` | 153 | 100 | 163 | 179 | 143 |
| `raft-closed-5v-16KiB-c64` | 183 | 346 | 582 | 686 | 183 |
| `raft-open-3v-128B-50pct` (95/s offered) | 95 | 37 | 66 | 73 | — |
| `raft-open-3v-128B-90pct` (170/s offered) | 170 | 70 | 162 | 185 | — |
| `raft-open-3v-128B-120pct` (227/s offered) | 140 | 5243 | 9634 | 9744 | — |
| `slow-follower-3v-128B-c16` | 180 | 85 | 146 | 161 | 168 |
| `slow-follower-5v-128B-c16` | 141 | 102 | 178 | 2081 | 143 |

Other figures from the same report:

- **Write amplification.** Bytes appended per acknowledged payload byte: 1.25 for 128 B entries and 1.002 for 16 KiB,
  per node (1.71 in the overloaded open-loop cell, where appended entries were never acknowledged). The cluster ratio
  scales with the voter count (3.75 for three voters with 128 B entries). `clusterFileRatio` reaches 141 for
  small-entry Raft cells, because each node preallocates 4 MiB chunks; it is close to 1 for long 16 KiB cells.
- **Commit pipeline.** The WAL commit checkpoint flush (`checkpointFlushDuration`) is p50 about 5 ms in wal cells and
  single-client Raft cells, 7 to 10 ms in replicated cells under concurrency, and 18 to 24 ms in single-node Raft
  cells and 16 KiB batches. `applyDuration` p99 is at most 0.4 ms. In `raft-closed-3v-128B-c16`, followers apply an
  index p50 85 ms and p99 874 ms after the first node, and at most 155 entries behind.
- **Slow follower.** Three voters: the slow follower installed 9 snapshots (1.08 MB through the relay) and, with the
  other two, recovered all 5,416 entries from disk. Five voters: 8 snapshot installs (0.9 MB), 4,255 of 4,255
  recovered on every node. Its p99.9 ack latency was 2.1 s in five-voter runs, both times.
- **Overload.** At 120% of the busiest closed-loop rate, completion fell to 140/s (62% of the 227/s offered), in-flight
  writes reached 2,209, the leader held up to 1,510 uncommitted entries, and the cell went through 3 leader terms. 2,133
  writes ended with an unknown outcome after leadership changed (most likely the 1 to 2 s election timeout fired while
    heartbeats waited behind appends; the tool does not show the cause). The oracles still passed. The 50% and 90% cells kept up with the offered rate.

Observations, for follow-up rather than for this tool:

- **WAL appends do not scale with concurrency.** `wal-append` gains at most about 2.7x from c1 to c64, because each
  `AppendAsync` runs its flushes under one lock (`PersistAppendAsync`). Batched appends (`wal-batch`, one
  `AppendAsync` for many entries) reach 4,000 to 16,000 entries/s.
- **A single-node cluster gets slower with more clients.** `raft-closed-1v-128B` falls from 100/s at c1 to 47/s at c64
  (54/s and 45/s in run 1). Replicated clusters do not, perhaps because there the leader's appends overlap with
  replication.
- **Run-to-run spread is large** for the 16 KiB WAL cells and the c1 cells (`raft-closed-3v-16KiB-c1` was 6/s with an
  815 ms p99 in run 1, 58/s in run 2). Repeat a run before treating a difference as a regression.

## Blind spots

- **Power loss and page-cache loss.** The recovery audit reopens the files in the same OS instance; it cannot tell
  whether the device honours a flush. Stage 3.
- **Process kill and restart under load,** and recovery after a crash mid-append. Stage 3.
- **Cross-process isolation.** All nodes share one process: CPU, GC, thread pool and allocations are per process, not
  per node, and one node's GC pause stalls the others.
- **The HTTP and UDP transports,** and real network latency, loss and partitions. The relay only delays and pauses one
  follower's TCP traffic.
- **Membership changes under load,** and I/O faults (`ENOSPC`, `EIO`, torn writes).
- **Leader-side batching of client proposals** is not exposed through the public API, so it is not measured.
- **Portability.** A baseline describes one machine, one filesystem and one device. Compare runs on the same host only.
