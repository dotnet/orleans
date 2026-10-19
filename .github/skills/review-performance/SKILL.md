---
name: review-performance
description: Performance lens for Orleans — back-of-envelope cost estimates, bottleneck identification, batching, control/data plane separation, and GC-aware hot paths in messaging, serialization, scheduling, and per-activation state. Use during code review, and when designing or implementing performance-sensitive code.
---

# Performance lens

Apply with root `AGENTS.md`, including its allocation and retained-memory
guidance. This lens examines resource cost: network, disk, memory, and CPU, each
by bandwidth and latency.

## Procedure

1. Sketch the back-of-envelope cost of the change, multiplied by realistic counts (activations, messages per second, silos).
2. Identify the slowest resource on the path, weighted by frequency.
3. Prefer design-level wins (batching, control/data plane separation, locality) to micro-optimizations.
4. Measure before and after with the BenchmarkDotNet projects under `test/Benchmarks`, and report the numbers.
5. Keep correctness and simplicity intact; a faster design that weakens an invariant is a regression.

## Checklist

- **Back-of-envelope at design time.** The largest wins come from design; estimate network, disk, memory, and CPU cost before optimizing code.
- **Slowest resource first.** Measure and focus on the few bottleneck operations, weighted by frequency.
- **Batch.** Amortize per-operation cost for storage writes, messages, and directory operations.
- **Separate planes.** Control-plane work (membership, directory, placement) stays off the data plane (message dispatch, serialization, grain turns).
- **Run at your own pace.** External events are enqueued and processed in batches.
- **Simple algorithms for small N.** N is usually small; complexity is justified by measured N.
- **GC-aware hot paths.** Steady state on messaging, serialization, and scheduling paths is allocation-free, using pooled buffers, pre-sized collections, `Span<T>`, and structs where they pay off.

## Findings

Report findings ordered by expected impact, each with the location, a cost
estimate, and a concrete fix.
