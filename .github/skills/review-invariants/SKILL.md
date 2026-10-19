---
name: review-invariants
description: Correctness and invariants lens for Orleans — invariants and assertions, error handling, failure semantics, idempotence, grain concurrency and reentrancy, async and cancellation, security boundaries, and observability. Use during code review, and when designing or implementing runtime or provider code.
---

# Invariants lens

Apply with root `AGENTS.md`, including its assertion policy. This lens examines
what must always be true, who guarantees it, and what happens when something fails.

## Procedure

1. For each affected component, list the invariants it **provides** to consumers and the invariants it **demands** of callers and lower layers.
2. Classify every error path: operating error (handle it) or programmer error (assert it).
3. Replace fallbacks with invariants: an impossible condition is asserted; a possible condition belongs in the spec with a designed path.
4. When an invariant almost holds, or it is unclear whether it should hold, raise it as a question before code depends on it.
5. Check failure semantics, idempotence, concurrency ownership, cancellation flow, and trust boundaries.

## Checklist

- **Invariants in both directions.** Each component states what it guarantees and what it requires, in types, XML docs, and assertions.
- **Invariants, not fallbacks.** A fallback masks a broken invariant; every handled condition has a designed path.
- **Designed hints.** Caches, directory and routing entries, and placement hints are specified as possibly stale, carry a cheap validity check, and have a defined correct path.
- **Illegal states unrepresentable.** External input is parsed once at the boundary and trusted inside ([parse, don't validate](https://lexi-lambda.github.io/blog/2019/11/05/parse-don-t-validate/)).
- **Two kinds of error.** Operating errors (I/O, timeouts, silo failure, storage conflicts) are handled; programmer errors fail fast. Most catastrophic failures come from mishandled error paths ([Yuan et al.](https://www.usenix.org/system/files/conference/osdi14/osdi14-paper-yuan.pdf)).
- **Strong assertions.** `Debug.Assert` covers preconditions, postconditions, and invariants, in both positive and negative space; split compound assertions and [pair them across boundaries](https://tigerbeetle.com/blog/2023-12-27-it-takes-two-to-contract) (before write, after read).
- **Safety, then liveness.** Nothing bad happens unconditionally; progress is bounded by timeouts and progress checks.
- **Failure semantics.** Every interface defines timeout, cancellation, partial completion, duplicate, and staleness behavior; a timed-out grain call may still have executed.
- **Idempotence.** Stable operation ids and ETags make retries safe; retries are end-to-end.
- **Single-owner concurrency.** Prefer grain turns, sharding, and commuting updates to locks; prefer leases and epochs to distributed locks.
- **Interleaving.** Reentrant and interleaved calls (`[Reentrant]`, `[AlwaysInterleave]`, call-chain reentrancy) re-validate activation state after each `await`.
- **Activation affinity.** Activation state changes only within the activation's turns; offloaded work (`Task.Run`, `ConfigureAwait(false)` continuations) returns results to the turn.
- **Determinism.** Time (`TimeProvider`), randomness, scheduling, and I/O are injected so executions reproduce.
- **Async and cancellation.** Cancellation flows end-to-end; async code awaits instead of blocking (`.Result`, `.Wait()`, `GetAwaiter().GetResult()`).
- **Ownership and lifetime.** Every resource (timer, subscription, connection, `IDisposable`) has an explicit owner and a lifetime tied to the activation or silo lifecycle.
- **Security.** The trusted computing base stays small, privileges are least, and untrusted input (network, storage, configuration, serialized payloads) is parsed once at the trust boundary.
- **Observability.** Logs, metrics, and traces are part of the spec; each failure path logs enough context (grain id, silo, operation) to diagnose without a debugger, using `[LoggerMessage]`.

## Findings

Report the location, the violated or missing invariant, and a concrete fix. List
invariant questions separately.
