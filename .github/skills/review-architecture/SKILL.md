---
name: review-architecture
description: Architecture lens for Orleans — specs, abstractions, layering, end-to-end placement of guarantees, virtualization, authoritative state, dependencies, and compatibility. Use during code review, and when designing features, APIs, or layer changes.
---

# Architecture lens

Apply with root `AGENTS.md`. This lens examines the shape of the system: specs,
abstractions, layers, contracts, and where each guarantee lives.

## Procedure

1. Start from the affected contract: client-visible behavior for observable changes, or the relevant internal guarantees for implementation-only changes.
2. Name each affected layer, the contract it provides upward, and what it requires from below.
3. Assign each guarantee (durability, ordering, deduplication, security) to the layer that can verify the complete outcome.
4. Trace the logical identities, the physical instances behind them, and the indirections that connect them.
5. Determine whether each affected surface has shipped in a stable release; that selects the compatibility rules below.
6. Flag complexity that the spec, an established contract, compatibility, diagnostics, or measured cost does not justify.

## Design

- **Spec first.** New or changed behavior has a stated contract before code; stateful components model it as client-visible state and actions (a state machine) ([Lampson](https://arxiv.org/abs/2011.02455)).
- **Interfaces are contracts.** An interface documents what callers rely on beyond its signature: behavior, failure modes, ordering, and resource or performance assumptions, as they apply.
- **Modularity.** Each module hides a design decision likely to change, so callers never depend on its representation ([Parnas](https://www.win.tue.nl/~wstomv/edu/2ip30/references/criteria_for_modularization.pdf)).
- **Composable primitives.** Small primitives compose, and a composite preserves the contract it implements.
- **Layering.** Dependencies point down, toward the runtime core and abstractions; each layer exposes lower-layer capabilities through its own contract or an explicit extension point, and code that bypasses that contract to reach a lower layer is a layering violation.
- **Weakest sufficient contract.** A layer guarantees only what callers require and it can enforce; callers needing more compose it (for example, effectively-once processing from retried at-most-once calls plus idempotent handlers).
- **End-to-end.** Reliability, security, deduplication, and ordering are verified by the endpoints that observe the complete outcome; lower-layer guarantees reduce cost and are correctness dependencies only where a contract states them ([Saltzer et al.](https://web.mit.edu/Saltzer/www/publications/endtoend/endtoend.pdf)).
- **Virtualization.** Application code addresses grains by logical identity; runtime code reaches physical activations only through placement, directory, lifecycle, and migration contracts.
- **Mechanism vs policy.** The runtime and providers supply mechanisms; policy that callers have a concrete need to choose is exposed through options, placement strategies, or providers.
- **Single source of truth.** Each piece of state has one authority at a time (membership table, directory partition owner, grain storage), with transfers governed by versions or epochs; caches and hints are derived and refreshable from that authority.
- **Reuse proven patterns.** Logs, state machines, epochs and ETags, leases, idempotency keys, sharding, and batching are applied where their guarantees and costs fit the problem.
- **Divide and conquer.** Where scale or contention requires concurrency, state is sharded by key and work pipelined into stages before shared locks are considered.
- **Reconfiguration is a protocol.** Ownership and topology changes seal the old configuration, define the cutover point, and switch via one authoritative versioned record.
- **Minimal dependencies.** The BCL and packages already in `Directory.Packages.props` are preferred; a new dependency is justified by a requirement they cannot meet.

## Compatibility

- **Shipped surfaces are contracts.** Shipped public APIs, serialized contracts (`[Id]`, `[Alias]`, type names), and persisted grain state and storage schemas evolve additively; breaks and migrations follow an explicit, approved process (see package compatibility in `AGENTS.md`).
- **Rolling upgrades.** Changes to messages and persisted data interoperate across the supported rolling-upgrade window, tested in both directions (old writer to new reader, and new writer to old reader).
- **Observable behavior.** Documented behavior, and observable behavior users plausibly depend on, changes deliberately with its impact stated ([Hyrum's law](https://www.hyrumslaw.com/)).
- **Unshipped surfaces.** APIs and formats introduced since the last stable release carry no shipped-compatibility obligation and stay consistent with in-repository callers and the approved design.
- **Uncertain status.** When shipped or persisted status is unclear, raise it as a question.

## Findings

Report the location, the principle at stake, and a concrete fix. List open
questions about invariants or shipped status separately.
