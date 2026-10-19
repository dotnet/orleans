---
name: review-architecture
description: Architecture lens for Orleans — specs, abstractions, layering, end-to-end placement of guarantees, virtualization, single source of truth, dependencies, and compatibility. Use during code review, and when designing features, APIs, or layer changes.
---

# Architecture lens

Apply with root `AGENTS.md`. This lens examines the shape of the system: specs,
abstractions, layers, contracts, and where each guarantee lives.

## Procedure

1. State the client-visible state and actions (the spec) before judging structure.
2. Name each affected layer or module, the contract it provides upward, and what it requires from below.
3. Place each guarantee (durability, ordering, deduplication, security) at the endpoint that can verify it.
4. Identify the logical/physical separation and the single indirection between them.
5. Determine whether each affected surface has shipped in a stable release; that selects the compatibility rules below.
6. Prefer the simplest design that meets the spec; flag anything the spec does not require.

## Design

- **Spec first.** Client-visible state and actions (a state machine) precede code; "without a spec, a system cannot be wrong, only surprising" ([Lampson](https://arxiv.org/abs/2011.02455)).
- **Interfaces are contracts.** A contract states state, behavior, failure modes, ordering, and resource and performance assumptions; a signature alone is an incomplete spec.
- **Modularity.** Each module hides one design decision likely to change ([Parnas](https://www.win.tue.nl/~wstomv/edu/2ip30/references/criteria_for_modularization.pdf)).
- **Composable primitives.** Small primitives compose; a composite preserves the contract it implements.
- **Layering.** Dependencies point down; a layer exposes the power of the layer below through its own contract, and reaching around a layer is a layering violation.
- **Weakest sufficient contract.** A layer provides only what callers need; callers derive stronger guarantees.
- **End-to-end.** Reliability, security, deduplication, and ordering live at the endpoints that can verify them; lower-layer guarantees are performance optimizations ([Saltzer et al.](https://web.mit.edu/Saltzer/www/publications/endtoend/endtoend.pdf)).
- **Virtualization.** Grain identity is logical and activations are physical; placement, directory, activation, deactivation, migration, and recovery live behind the grain id → activation indirection, and callers depend only on identity.
- **Mechanism vs policy.** The runtime and providers supply mechanisms; callers choose policy through options, placement strategies, and providers.
- **Single source of truth.** Each piece of state has one authoritative owner (membership table, directory partition owner, grain storage); caches and hints are derived soft state, reconstructible from the owner.
- **Reuse proven patterns.** Logs, state machines, epochs and ETags, leases, idempotency keys, sharding, and batching apply at every scale.
- **Divide and conquer.** Shard state by key and pipeline work into stages.
- **Reconfiguration is a protocol.** Seal the old configuration, determine the cut point, and switch via one authoritative versioned record.
- **Minimal dependencies.** Prefer the BCL and packages already in `Directory.Packages.props`; a new dependency carries explicit justification and maintainer approval.

## Compatibility

- **Shipped surfaces are contracts.** Wire formats (serialization `[Id]`, `[Alias]`, type names), persisted grain state and storage schemas, and public APIs evolve additively and version explicitly.
- **Rolling upgrades.** Mixed-version clusters interoperate; changes to messages or persisted data are tested across versions.
- **Hyrum's law.** Observable behavior becomes depended upon ([Hyrum's law](https://www.hyrumslaw.com/)); behavior changes are deliberate and documented.
- **Unshipped surfaces.** APIs and formats introduced since the last stable release change freely until they ship.
- **Uncertain status.** When shipped or persisted status is unclear, raise it as a question.

## Findings

Report the location, the principle at stake, and a concrete fix. List open
questions about invariants or shipped status separately.
