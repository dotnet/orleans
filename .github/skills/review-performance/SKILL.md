---
name: review-performance
description: Performance lens for Orleans — back-of-envelope cost estimates, bottleneck identification, batching, control/data plane separation, and GC-aware hot paths in messaging, serialization, scheduling, and per-activation state. Use during code review, and when designing or implementing performance-sensitive code.
---

# Performance lens

Apply with root `AGENTS.md`, including its allocation and retained-memory
guidance. This lens examines resource cost: network, disk, memory, and CPU, each
by bandwidth and latency.

## Procedure

1. For performance-sensitive changes (hot paths, per-message or per-activation state, scale-dependent work), estimate the dominant costs at realistic counts of activations, messages per second, and silos.
2. Identify the slowest resource on the path, weighted by frequency.
3. Prefer design-level wins (batching, control/data plane separation, locality) to micro-optimizations.
4. Support performance claims with comparable before-and-after measurements: BenchmarkDotNet projects under `test/Benchmarks` for local code paths, and load or integration measurements for distributed behavior.
5. Keep correctness and simplicity intact; a faster design that weakens an invariant is a regression.

## Checklist

- **Back-of-envelope at design time.** The largest wins come from design, so new designs and scale-dependent changes start from an estimate of network, disk, memory, and CPU cost.
- **Slowest resource first.** Effort goes to the few bottleneck operations, identified by measurement and weighted by frequency.
- **Tail latency.** Fan-out and background contention are evaluated through end-to-end latency percentiles under representative load ([The Tail at Scale](https://research.google/pubs/the-tail-at-scale/)).
- **Bounded speculation.** Retries and hedges share explicit attempt, concurrency, and time budgets with the original request; duplicate execution and work continuing after cancellation preserve the operation's contract.
- **Batch.** Per-operation overhead (storage writes, messages, directory operations) is amortized across batches where latency, ordering, and partial-failure semantics permit.
- **Separate planes.** Control-plane work (membership, directory, placement) stays out of per-message and per-turn paths, which use cached decisions with defined refresh.
- **Run at your own pace.** A component that receives external events processes them in batches at its own rate, rather than doing full work per event as it arrives, where latency permits.
- **Simple algorithms for small N.** Where N is small in practice (per-activation collections, silo counts), the simplest algorithm wins; scale-dependent N (activations, messages, subscriptions) justifies better complexity.
- **GC-aware hot paths.** On hot paths (messaging, serialization, scheduling), avoidable steady-state allocations are removed with pooled buffers, pre-sized collections, `Span<T>`, and structs where measurement shows net benefit.

## Findings

Report findings ordered by expected impact, each with the location, a cost
estimate or measurement with its stated assumptions, and a concrete fix.
