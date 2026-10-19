---
name: review-invariants
description: Correctness and invariants lens for Orleans — invariants and assertions, error handling, failure semantics, idempotence, grain concurrency and reentrancy, activation scheduling, async and cancellation, security boundaries, and observability. Use during code review, and when designing or implementing runtime or provider code.
---

# Invariants lens

Apply with root `AGENTS.md`, including its assertion policy. This lens examines
what must always be true, who guarantees it, and what happens when something fails.

## Procedure

1. For each affected boundary, identify the guarantees material to the change, including implicit ones the code already relies on: what the component **provides** to consumers and what it **demands** of callers and lower layers.
2. Classify each changed error path: expected operational failure, invalid external input, cancellation, or programming-invariant violation.
3. Look for an invariant or redesign that eliminates each fallback; replace fallbacks for states that cannot occur with assertions; give each condition that can occur a designed path in the spec.
4. When an invariant almost holds, or it is unclear whether it should hold, ask the maintainer (in reviews, raise it as a question) before code depends on it.
5. Check failure semantics, idempotence, concurrency ownership, activation scheduling, cancellation flow, and trust boundaries.

## Checklist

- **Invariants in both directions.** Each affected boundary states what it guarantees and requires, using types where practical, XML docs for non-obvious caller-visible guarantees, and assertions for programming invariants.
- **Explicit guarantees.** Guarantees that code relies on are made explicit unless they are obviously maintained: in the type where the type system can express them, otherwise in a doc comment at the owning boundary and an assertion where they are relied on.
- **Invariants, not fallbacks.** Code relies on established invariants and asserts programming violations at their owning boundary. Prefer an invariant or simpler design that removes a condition; an operational alternative required by the contract has a reachable trigger, a defined outcome, and an oracle that exercises it. Yuan et al. found incorrect handling of non-fatal errors in 92% of the 48 catastrophic failures in their study ([source](https://www.usenix.org/system/files/conference/osdi14/osdi14-paper-yuan.pdf)).
- **Designed hints.** Caches and directory, routing, and placement entries are specified as possibly stale, define how staleness is detected (validity check, version, or rejection by the authority), and recover through the authoritative path.
- **Illegal states unrepresentable.** External input is converted to validated types at the owning boundary and trusted inside, and revalidated when it crosses another trust boundary ([parse, don't validate](https://lexi-lambda.github.io/blog/2019/11/05/parse-don-t-validate/)).
- **Two kinds of error.** Expected failures (I/O, timeouts, silo failure, storage conflicts, invalid external input, cancellation) are handled explicitly; product programming errors are caught by side-effect-free `Debug.Assert` per `AGENTS.md`. Handlers that are empty or only log, catch overly general exceptions, or are left as `TODO` are defects; these three patterns caused 35% of the catastrophic failures Yuan et al. studied.
- **Precise assertions.** Each assertion states one precondition, postcondition, or invariant, so a failure identifies the broken term, and covers both what must hold and what must not (for example, an entry is present and not yet disposed).
- **Paired assertions.** An invariant on data crossing a boundary is asserted on both sides: before it is written or sent, and after it is read or received ([pair assertions](https://tigerbeetle.com/blog/2023-12-27-it-takes-two-to-contract)).
- **Safety, then liveness.** Safety holds for every execution admitted by the fault model; progress follows stated quorum, scheduling, and communication assumptions, with deadlines or stall detection where the contract requires them.
- **Failure semantics.** Each interface that can time out, be cancelled, partially complete, see duplicates, or return stale data defines that behavior; a timed-out grain call may still have executed.
- **Idempotence.** Retried operations define duplicate behavior end to end, using natural idempotence, stable operation IDs, ETags or versions, or deduplication.
- **Single-owner concurrency.** Single-owner state (grain turns), sharding, and commutative (order-independent) updates are preferred to locks; leases and epochs, rather than distributed locks, govern ownership that moves between silos.
- **Fencing.** The protected resource checks ownership generation as part of applying a mutation, so work from a superseded owner cannot commit after the cutover ([Chubby](https://research.google/pubs/the-chubby-lock-service-for-loosely-coupled-distributed-systems/)).
- **Virtual synchrony.** Each operation takes effect within the configuration that accepted it or not at all: the reconfiguration decision fixes the old configuration's closing state (for example, grain directory partition handoff on a membership change), so in-flight work from a sealed configuration never becomes visible later ([Birman, Malkhi, and van Renesse](https://www.microsoft.com/en-us/research/publication/virtually-synchronous-methodology-for-dynamic-service-replication/)).
- **Recovery eligibility.** A recovering participant resumes each role after restoring the durable evidence or completing the rebuild protocol that establishes that role's safety guarantees ([Paxos Made Live](https://research.google/pubs/paxos-made-live-an-engineering-perspective-2006-invited-talk/)).
- **Interleaving.** In reentrant or interleaved paths (`[Reentrant]`, `[AlwaysInterleave]`, call-chain reentrancy), assumptions about mutable activation state are re-checked after each `await` at which another turn can run.
- **Activation scheduling.** Grain state is accessed only on the activation's scheduler: grain code awaits on the captured context (`ConfigureAwait(false)` is flagged by `ORLEANS0014`), `Task.Run` work returns results without touching grain state, and external callbacks enter the activation through `RunRequestAsync`.
- **Determinism.** Time (`TimeProvider`), randomness, and external I/O that tests must control are injected so executions reproduce.
- **Async and cancellation.** Operations that accept a `CancellationToken` pass it to every cancellable callee; async code awaits instead of blocking (`.Result`, `.Wait()`, `GetAwaiter().GetResult()`).
- **Ownership and lifetime.** Long-lived resources (timers, subscriptions, connections, `IDisposable`s) have one owner and are released at that owner's boundary: request, activation, component, provider, or silo.
- **Security.** The trusted computing base stays small, components hold least privilege, and data is validated according to its trust level before it influences privileged or persistent state.
- **Observability.** Operationally significant failures are diagnosable from logs, metrics, or traces emitted at the owning boundary with stable context (grain ID, silo, operation); repeated product log messages use `[LoggerMessage]`.

## Findings

Report the location, the violated or missing invariant, and a concrete fix. List
invariant questions separately.
