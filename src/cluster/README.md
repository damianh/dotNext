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

The first durable update upgrades legacy raw-index/version-0 checkpoints to
version 1. This is a **one-way store upgrade**: older binaries reject the new
checkpoint version. Preserve a backup before upgrading if a binary rollback
is required; do not downgrade an active cluster by restoring stale node stores.
Legacy stores recover only their known committed/snapshot history. The upgrade
cannot certify old uncommitted page bytes or recover previously lost writes.
Back up a stopped WAL directory as a whole, including its checkpoint format
marker, publication intent, and overwrite journal when present.

The new checkpoint records appended and committed boundaries separately and
uses checksummed generations. Metadata replacement is protected by a durable
undo journal so an interrupted replacement cannot invalidate the preceding
acknowledged history. Storage errors fail the operation rather than producing
a positive replication acknowledgment. Durability depends on the filesystem
and device honoring the OS durable-write primitives; terminating a process
tests recovery, not a physical power failure or a device that lies about flushes.
Custom `IPersistentState` implementations remain responsible for their own
persistence guarantees; `ConsensusOnlyState` is intentionally in-memory.

# HyParView
List of supported features:
* Network transport: HTTP 1.1, HTTP/2, HTTP/3
* TLS support: HTTP 1.1, HTTP/2, HTTP/3
* Tight integration with ASP.NET Core framework
* Broadcasting support

Useful links:
* [Gossip messaging and peer discovery using HyParView](https://dotnet.github.io/dotNext/features/cluster/gossip.html)
* [API Reference](https://dotnet.github.io/dotNext/api/DotNext.Net.Cluster.Discovery.HyParView.html)