# Product

<!-- impeccable:product-schema 1 -->

## Platform

web

## Users

The product serves a deliberately layered audience. Beginners follow a guided path that assumes no
implementation knowledge. Experienced distributed-systems engineers and dotNext contributors can reveal
the library internals, source links, protocol details, and advanced scenarios without changing the default
learning flow.

## Product Purpose

Interactive explainers teach Raft fundamentals, show how dotNext implements them, and demonstrate the
correctness guarantees carried by this fork. A successful chapter lets the learner predict the next
protocol event, manipulate timing or message delivery, and immediately test that prediction against the
real state machine.

## Positioning

The browser runs real `RaftCluster<TMember>` instances from the current dotNext source rather than a
separate visual approximation. Guided scenarios, an inspectable protocol trace, and optional source-level
detail connect the conceptual model to production implementation.

## Operating Context

Readers progress through short chapters, each centered on one protocol invariant or implementation idea.
They pause, step, replay, deliver or drop messages, create partitions, and inspect node state. An eventual
sandbox supports unguided experiments with three to seven nodes and shareable seeds.

## Capabilities and Constraints

- The implementation is a Blazor WebAssembly site under `site/`, hosted on GitHub Pages.
- The simulation engine references the dotNext cluster project directly and uses only its public API.
- A purpose-built in-memory `IPersistentState` will expose log payloads and distinguish volatile from
  durable state for crash/restart lessons.
- The write-ahead log uses memory-mapped files and therefore cannot execute in WebAssembly; its chapter is
  an interactive illustration rather than a live WAL.
- Beginner guidance is the default. dotNext implementation details are progressive disclosure.
- Advanced fork chapters show correct fork behavior and its pinned invariant, not buggy upstream behavior.
- No generated WebAssembly binaries are committed; CI publishes the site artifact directly.

## Evidence on Hand

- Production Raft implementation:
  `src/cluster/DotNext.Net.Cluster/Net/Cluster/Consensus/Raft/`
- Deterministic in-process testing patterns:
  `src/DotNext.Tests/Net/Cluster/Consensus/Raft/InProcess/`
- Fork correctness findings and regression coverage: `RAFT-REVIEW.md`
- Fork behavior contract: `FORK-DIVERGENCE.md`
- A feasibility spike in `site/` builds, trims, and AOT-compiles the real cluster library for browser WASM.

No testimonials, learner research, usage analytics, or established visual identity for the explainers
exist yet; future work must not fabricate them.

## Product Principles

1. Ask for a prediction before revealing the protocol outcome.
2. Run the real implementation wherever the browser platform permits.
3. Make causality visible: every state change should connect to a timer, message, or durable write.
4. Keep the first reading approachable while making implementation depth one deliberate action away.
5. Prefer small, replayable experiments over passive explanation.

## Accessibility & Inclusion

Every protocol event and state must be available as text, not color or motion alone. The experience must
support keyboard operation, reduced motion, responsive layouts, and readable traces at high zoom.
