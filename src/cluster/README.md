.NEXT Cluster Programming Suite
====
.NEXT Cluster Programming Suite is a set of libraries for building clustered microservices:
* [DotNext.Net.Cluster](https://www.nuget.org/packages/DotNext.Net.Cluster/) contains cluster programming model, transport-agnostic implementation of Raft algorithm, TCP and UDP transport bindings for Raft, transport-agnostic implementation of HyParView membersip protocol for Gossip-based messaging
* [DotNext.AspNetCore.Cluster](https://www.nuget.org/packages/DotNext.AspNetCore.Cluster/) is a concrete implementation of Raft and HyParView algorithms on top of _DotNext.Net.Cluster_ library for building ASP.NET Core applications

# Raft
List of supported features:
* Network transport: TCP, UDP, HTTP 1.1, HTTP/2, HTTP/3, custom transport on top of [ASP.NET Core Connections](https://docs.microsoft.com/en-us/dotnet/api/microsoft.aspnetcore.connections) abstraction
* TLS support: TCP, HTTP 1.1, HTTP/2, HTTP/3
* High-performance, general-purpose [Persistent Write-Ahead Log](https://dotnet.github.io/dotNext/features/cluster/wal.html) supporting log compaction
* Replication of log entries across cluster nodes
* Tight integration with ASP.NET Core framework
* Friendly to Docker/LXC/Windows containers
* Everything is extensible
    * Custom write-ahead log
    * Custom network transport
    * Cluster members discovery

Useful links:
* [Overview of Cluster Programming Model](https://dotnet.github.io/dotNext/features/cluster/index.html)
* [Cluster Programming using Raft](https://dotnet.github.io/dotNext/features/cluster/raft.html)
* [API Reference](https://dotnet.github.io/dotNext/api/DotNext.Net.Cluster.Consensus.Raft.html)

## WAL durability and upgrades

`WriteAheadLog` completes an append only after persisting its data, metadata,
and recoverable appended boundary. The published `LastEntryIndex` therefore
represents durable entries, including entries not yet committed. This applies
to single and batch appends, replacement tails, and snapshot installation,
with shared memory, private memory, and supported direct I/O.

`CommitAsync` advances logical commitment and schedules application.
`FlushAsync` captures the committed index at invocation and waits for that
committed checkpoint; later appends do not enlarge this target. A concurrent
snapshot can supersede it. `FlushInterval` controls committed checkpoints,
not append durability: `Timeout.InfiniteTimeSpan` requires explicit committed
flushing but does not allow successful appends to remain buffered. Applications
should account for the additional append latency and prefer batch appends
where appropriate. Recovered uncommitted entries are neither committed nor
applied until Raft subsequently commits them.

The first durable update upgrades legacy raw-index/version-0 checkpoints and
upstream dotNext 6.8 version-1 checkpoints to version 2. This is a
**one-way store upgrade**: older binaries, including upstream dotNext, reject the new
checkpoint version. Preserve a backup before upgrading if a binary rollback
is required; do not downgrade an active cluster by restoring stale node stores.
Legacy version-0 stores recover only their known committed/snapshot history;
upstream version-1 stores also recover their recorded flushed tail. The upgrade
cannot certify old uncommitted page bytes or recover previously lost writes.
Back up a stopped WAL directory as a whole, including its checkpoint format
marker, publication intent, and overwrite journal when present.
See [FORK-DIVERGENCE.md](../../FORK-DIVERGENCE.md) for all fork behaviour that differs
from upstream dotNext.

The new checkpoint records appended and committed boundaries separately and
uses checksummed generations. Metadata replacement is protected by a durable
undo journal so an interrupted replacement cannot invalidate the preceding
acknowledged history. Storage errors fail the operation rather than producing
a positive replication acknowledgment. Durability depends on the filesystem
and device honoring the OS durable-write primitives; terminating a process
tests recovery, not a physical power failure or a device that lies about flushes.
Custom `IPersistentState` implementations remain responsible for their own
persistence guarantees; `ConsensusOnlyState` is intentionally in-memory.

## Supported storage and crash model

**Crash model.** The guarantees above are for *process* failure: the process is
killed or disposed at any point and the files are reopened, with the operating
system and storage still intact. Power loss, kernel panic and device faults are
**not** simulated or tested. They are supported only to the extent that the
platform honours the assumptions listed below.

**Term and vote record.** `WriteAheadLog` keeps `currentTerm` and `votedFor` in
the file `state`: one 37-byte in-place record at offset 0 (presence byte,
`ClusterMemberId`, term). It has no checksum. The file is opened with
`FileOptions.WriteThrough` and every update is one `RandomAccess.WriteAsync` of
the whole record, serialized by a lock. The contract, guarded by
`TermVoteDurabilityTests`:

- A vote is never granted, and a reply never carries `Value = true`, before the
  vote is durable. A failed or cancelled write leaves the node free to vote
  again; nothing was granted.
- The in-memory (published) term and vote are updated only after the write has
  completed. Because the published term is the one acknowledged in RPC replies,
  a term is never advertised before it is durable, so a restart cannot revert to
  a term the node already acknowledged. If a write fails after the bytes reached
  the disk, the disk is ahead of memory, which is safe: the durable term only
  rises and a grant still needs a successful write.

**Platform assumptions (documented, not tested).**

- *Sector atomicity.* The 37-byte write is assumed not to tear. A 37-byte write
  at offset 0 normally lands in one 512 B or 4 KiB sector, but no universal
  atomicity guarantee is asserted and no checksum detects a torn record.
- *Directory fsync on first creation.* The `state` file is created with
  `FileMode.CreateNew`; the directory entry is not fsynced. On Linux, a power
  loss right after the first boot could lose the file, and a vote granted in
  that window would be forgotten (reset to term 0, no vote). Process termination
  is not affected.
- *`WriteThrough` per platform.* .NET maps `FileOptions.WriteThrough` to
  `FILE_FLAG_WRITE_THROUGH` on Windows and to `O_SYNC` on Unix
  (`SafeFileHandle.Unix.cs` in dotnet/runtime). `O_SYNC` makes each write
  durable on Linux with a filesystem and device that honour it. On macOS
  `O_SYNC` does not imply `F_FULLFSYNC`, so the data can still be in the drive
  cache: macOS is not a supported platform for power-loss durability.
- *Short `state` file.* When `state` exists but is shorter than 37 bytes, the
  constructor zeroes the buffer and rewrites it, which silently resets the node
  to term 0 with no vote. Within the process-crash model only a length of 0 is
  reachable (a crash inside the very first creation, before any vote was
  possible). Lengths of 1 to 36 need power loss or external damage. Failing
  closed on those lengths is a possible hardening and is not implemented.
# HyParView
List of supported features:
* Network transport: HTTP 1.1, HTTP/2, HTTP/3
* TLS support: HTTP 1.1, HTTP/2, HTTP/3
* Tight integration with ASP.NET Core framework
* Broadcasting support

Useful links:
* [Gossip messaging and peer discovery using HyParView](https://dotnet.github.io/dotNext/features/cluster/gossip.html)
* [API Reference](https://dotnet.github.io/dotNext/api/DotNext.Net.Cluster.Discovery.HyParView.html)