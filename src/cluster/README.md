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

## One-way message deduplication

The HTTP Raft transport keeps a bounded, expiring request journal in each
process for one-way custom messages. A sender reuses the request ID when it
retries the same message. The receiving member drops the retry only while that
ID remains in its local journal. Eviction, expiration, process restart, or a
retry routed to another member removes that protection, so the message can be
delivered again.

A nonpositive `requestJournal:expiration` is rejected at startup; a value too
large to add to the current time keeps entries until eviction or restart.

This journal suppresses transport retries; it does not provide durable
exactly-once delivery or business idempotency. Operations that must tolerate
repeat execution should carry an application-level idempotency key and
deduplicate when applying the operation. In particular, a failed or cancelled
`RaftCluster.ReplicateAsync` proposal has an unknown outcome and must not be
made safe by relying on this request journal.

The request ID is not a credential. The journal provides neither
authentication nor cryptographic replay protection; those protections belong
to the host and its transport security configuration.

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
  vote is durable. A failed or cancelled write reports no grant. After an
  ambiguous failure (the bytes reached the disk anyway) the candidate may be
  durably recorded although no grant was reported; the node then honours that
  record and refuses other candidates in that term, which costs availability only.
- The in-memory (published) term and vote are updated only after the write has
  completed. Because the published term is the one acknowledged in RPC replies,
  a term is never advertised before it is durable, so a restart cannot revert to
  a term the node already acknowledged. If a write fails after the bytes reached
  the disk, the disk is ahead of memory. The failed operation reads the record back
  and publishes it (what is on disk is durable), so RPCs do not keep acting on an
  older term. If that read fails too, the record stays suspect and the next
  term/vote write reads it back first, failing if it differs from what the
  request was decided on, so a write never lowers a higher durable term. Known
  residual gap: until that next write, an `AppendEntries` at the old published
  term performs no write and can still be acknowledged. It needs a failed write
  that reached the disk followed by a failed read of the same file, which means the
  storage is failing broadly; RPCs are not made to fail closed while the record is
  suspect.

**Applied cluster configuration.** `PersistentClusterConfigurationStorage`
holds the last applied configuration as the committed baseline used on startup
and after snapshot installation. It is not always rebuildable from the WAL:
compaction can remove configuration entries covered by a snapshot, and the
state-machine snapshot does not contain them.

Every save writes a same-directory temporary file, flushes it, atomically
publishes it, and flushes the directory through `DurableFile` (#106). The
directory barrier runs on Windows, Linux, macOS and FreeBSD and is repeated when
an existing file is reopened. After a post-rename barrier failure, the same
instance retries the complete publication barrier before its next load or save.
Stale temporary files matching
`<configuration-file>.*.tmp` are removed on reopen. Temporary files left by
older releases, which used unrelated random names, are not identified.

The on-disk format remains an 8-byte little-endian version followed by the
configuration payload. A file shorter than the version header fails closed with
`IntegrityException`; restore it from a backup, or remove the member and re-add
it with an empty WAL directory. Deleting only this file can restore an obsolete
configuration and is unsafe. The payload has no checksum; truncation or
corruption after the complete header is detected only when configuration
decoding and validation reject it.

**Platform assumptions (documented, not tested).**

- *Sector atomicity.* The 37-byte write is assumed not to tear. A 37-byte write
  at offset 0 normally lands in one 512 B or 4 KiB sector, but no universal
  atomicity guarantee is asserted and no checksum detects a torn record.
- *Directory fsync on first creation.* The `state` file is created with
  `FileMode.CreateNew`, and the directory is fsynced right after (#82), so the
  directory entry does not depend on `O_SYNC` of the file. Without it, a power
  loss right after the first boot could lose the file and a vote granted in that
  window would be forgotten. It relies on the filesystem honouring directory fsync.
- *`WriteThrough` per platform.* .NET maps `FileOptions.WriteThrough` to
  `FILE_FLAG_WRITE_THROUGH` on Windows and to `O_SYNC` on Unix
  (`SafeFileHandle.Unix.cs` in dotnet/runtime). `O_SYNC` makes each write
  durable on Linux with a filesystem and device that honour it. On macOS
  `O_SYNC` does not imply `F_FULLFSYNC`, so the data can still be in the drive
  cache: macOS is not a supported platform for power-loss durability.
- *Short `state` file.* When `state` exists but is shorter than 37 bytes, the
  constructor used to zero the buffer and rewrite it, silently resetting the node
  to term 0 with no vote. Within the process-crash model only a length of 0 is
  reachable (a crash inside the very first creation, before any vote was
  possible), and that is still initialized. Lengths of 1 to 36 need power loss or
  external damage, and now fail closed (#83): the WAL does not open and throws
  `IntegrityException` naming the file.

## Host security

The library does not authenticate or authorize peers. Every Raft RPC
(PreVote, RequestVote, AppendEntries, InstallSnapshot, Synchronize, Resign,
Metadata) and every custom message sent through `IMessageBus` arrives at one
HTTP endpoint: the path of `publicEndPoint`, or `/cluster-consensus/raft` when
that has no path. Any caller that reaches this endpoint can change cluster
state. Protecting it is the host's responsibility. TLS is optional, and
server-authenticated HTTPS on its own does not authenticate callers.

**Choose a mechanism.** The library does not mandate one. Common options, alone
or combined:

- mutual TLS enforced by Kestrel or by an authenticating proxy or service mesh;
- an ASP.NET Core authentication scheme with an authorization policy that admits
  only cluster peers;
- network isolation, so that only cluster peers can reach the endpoint.

For outbound calls, attach peer credentials through `IHttpMessageHandlerFactory`.
The node creates its client handler by the name in `clientHandlerName`
(default `raftClient`).

**Register protection before the consensus handler.** `UseConsensusProtocolHandler`
maps the protocol path to a terminal branch. Middleware registered after it
never sees consensus requests, so it cannot protect them:

```csharp
app.UseAuthentication();
app.UseAuthorization();            // FallbackPolicy must admit only cluster peers
app.UseConsensusProtocolHandler();
```

The consensus handler is not a routed endpoint, so `[Authorize]` or
`RequireAuthorization()` metadata does not apply to it. Use the authorization
`FallbackPolicy`, your own gate middleware, mutual TLS, or a network-level
restriction. The upstream guide
([Cluster Programming using Raft](https://dotnet.github.io/dotNext/features/cluster/raft.html))
says that `UseConsensusProtocolHandler` "should be called before registration of
any authentication/authorization middleware". Do not follow that advice. With that
ordering an unauthenticated request reaches the handler and gets a successful
reply. `ConsensusHandlerHostSecurityTests` checks both orderings.

**Member IDs are not credentials.** The sender's member ID (the `X-Raft-Node-ID`
header) is self-asserted. Rejecting unknown member IDs is not a substitute for
authentication: a caller can copy a known ID, and an authorized joining node
needs catch-up traffic before it becomes a member (see below).

**Custom messages.** Messages delivered to `IInputChannel` handlers use the same
endpoint and are covered by the same host protection. A handler that needs
per-message authorization must perform it itself.

**Request deduplication is not replay protection.** The request journal
(`requestJournal`) is a bounded, expiring, process-local deduplication cache.
It is neither replay protection nor authentication.

**Leader redirection and forwarded headers.** `RedirectToLeader` keeps the
scheme, path and query of the incoming request and replaces the host and port
with the leader's address. A client cannot choose the destination host, so
the open-redirect claim (S4 in [RAFT-REVIEW.md](../../RAFT-REVIEW.md)) was
rejected. Trusting forwarded headers is a separate deployment decision. The
scheme comes from `HttpRequest.Scheme`, which forwarded-headers middleware can
rewrite, so accept `X-Forwarded-*` headers only from known proxies.

## Bootstrap and membership

**Authorization is not membership.** Host authorization decides whether a peer
may call the endpoint. Voting membership is the committed cluster configuration.
They are separate. When the leader adds a node (`AddMemberAsync`), it first
replicates its log to the node for `warmupRounds` rounds (default 10) and only
then commits a configuration that includes it. The host policy must therefore
admit an authorized joining node before it becomes a member.

**Exactly one node owns the cold start.** `coldStart` is `true` by default. It is
used only when the node's stored configuration is empty. The node then stores a
configuration that contains only itself. Unless it is also configured with
`standby: true`, it starts as the leader of that single-node cluster. Every empty
node started with `coldStart: true` stores its own single-node configuration, so
non-standby nodes form separate clusters. The configuration
sample in the upstream guide has `"coldStart" : true`, which is correct for
the first node only. Supported recipes:

- Start one node with `coldStart: true`. Start the others with `coldStart: false`.
  They wait in standby until the leader adds them with `AddMemberAsync`, or a
  `ClusterMemberAnnouncer<UriEndPoint>` registered on the joining node announces it.
- Pre-populate the same member list on every node before the first start. A
  non-empty configuration ignores `coldStart`.

`HttpBootstrapRecipeTests` checks both the hazard and the single-owner recipe.

**Persisted configuration.** `PersistentClusterConfigurationStorage` holds the
committed configuration baseline (see
[Supported storage and crash model](#supported-storage-and-crash-model)). A
configuration that has not been committed yet is held only in the WAL. Keep the
WAL and the configuration storage together. A node restarted with both survives
with `coldStart: true` unchanged and does not bootstrap again
(`HttpBootstrapRecipeTests.PersistedConfigurationPreventsBootstrapOnRestart`).
`InMemoryConfigurationStorage` loses the committed baseline on restart. If it is
not pre-populated, a restarted node with `coldStart: true` stores a new
single-node baseline. A newer configuration entry still in the WAL overrides that
baseline, so the node recovers its membership only if such an entry has not been
compacted away. Without one (for example, with a non-persistent or wiped WAL) the
node bootstraps a new single-node cluster. Never wipe a member's state and restart
it with `coldStart: true`. Remove the member and add it again instead.

**Timeout defaults (HTTP).**

| Setting | Default |
|---|---|
| `lowerElectionTimeout` / `upperElectionTimeout` | 150 ms / 300 ms |
| `requestTimeout` | `upperElectionTimeout` (300 ms) |
| `rpcTimeout` | `upperElectionTimeout` / 2 (150 ms) |
| connect timeout | `lowerElectionTimeout` (150 ms), only when no `IHttpMessageHandlerFactory` is registered |
| `warmupRounds` | 10 |

The defaults assume a low-latency network. TLS handshakes, mutual TLS, proxies or
links across zones may need larger values. If you raise the election timeouts,
raise `requestTimeout` and `rpcTimeout` with them. `rpcTimeout` must not exceed
`requestTimeout`, or the node fails to start. Use the same values on every member.

# HyParView
List of supported features:
* Network transport: HTTP 1.1, HTTP/2, HTTP/3
* TLS support: HTTP 1.1, HTTP/2, HTTP/3
* Tight integration with ASP.NET Core framework
* Broadcasting support

Useful links:
* [Gossip messaging and peer discovery using HyParView](https://dotnet.github.io/dotNext/features/cluster/gossip.html)
* [API Reference](https://dotnet.github.io/dotNext/api/DotNext.Net.Cluster.Discovery.HyParView.html)