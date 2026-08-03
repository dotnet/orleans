---
title: Activation collection and resource management
description: Manage idle Orleans activations and memory pressure.
ms.date: 08/02/2026
ms.topic: concept-article
---

# Activation collection and resource management

A grain activation is the in-memory instance that currently represents a grain identity. Orleans creates activations on demand and deactivates idle ones so a silo doesn't retain every grain it has ever served.

Activation collection is separate from .NET garbage collection. Orleans first deactivates the grain and removes runtime references; .NET GC can then reclaim the managed objects.

## Idle activation collection

An activation becomes eligible for collection after it has been idle for `GrainCollectionOptions.CollectionAge`. The default is 15 minutes. Orleans scans on the `CollectionQuantum`, which defaults to one minute, so deactivation isn't scheduled at an exact instant.

Incoming grain calls, reminders, and stream events count as activity. Outgoing calls and arbitrary application I/O don't keep an activation active for collection purposes. Timers don't keep an activation active by default, but a timer created with `GrainTimerCreationOptions.KeepAlive` resets activation idleness after each callback.

Configure a global age and targeted overrides:

:::code language="csharp" source="../snippets/hosting/HostingExamples.cs" id="activation_collection":::

Prefer type-specific changes over a cluster-wide increase. Longer ages trade memory for fewer activation and state-load operations.

## Request deactivation behavior

Call `DeactivateOnIdle()` when the current activation should deactivate after its current turn:

```csharp
this.DeactivateOnIdle();
```

Queued calls are forwarded to a new or existing activation.

Call `DelayDeactivation(duration)` to keep an activation from idle collection for at least a period:

```csharp
this.DelayDeactivation(TimeSpan.FromMinutes(30));
```

A negative duration cancels the previous delay. Delaying deactivation is an optimization, not a durability or placement guarantee. Failures, shutdown, migration, and resource pressure can still remove an activation.

Apply `[KeepAlive]` to a grain implementation only when the activation should be exempt from normal idle collection:

```csharp
[KeepAlive]
public sealed class ReferenceDataGrain : Grain, IReferenceDataGrain
{
    // ...
}
```

Keep-alive activations still consume memory and aren't a substitute for durable state.

## Memory-pressure activation shedding

Orleans can shed activations when process memory exceeds a configured percentage:

The defaults are:

| Option | Default |
|---|---:|
| `EnableActivationSheddingOnMemoryPressure` | `false` |
| `MemoryUsageLimitPercentage` | `80` |
| `MemoryUsageTargetPercentage` | `75` |
| `MemoryUsagePollingPeriod` | 5 seconds |

When enabled, Orleans estimates how many activations to deactivate to move from the limit toward the target, prioritizing older activations. Memory pressure can override normal keep-alive timing because protecting the process is more important than preserving an optimization.

Set container or process memory limits before tuning percentage thresholds. Leave enough space between target, limit, and the platform's hard limit for deactivation callbacks, state writes, and GC to complete.

## Activation and deactivation timeouts

`GrainCollectionOptions.ActivationTimeout` and `DeactivationTimeout` both default to 30 seconds. These are runtime safety bounds, not goals. Keep `OnActivateAsync`, `OnDeactivateAsync`, state access, and dependency calls cancellable and normally much faster.

## Tune from measurements

Track at least:

- Activation count and activation/deactivation rate by grain type.
- State-load and state-write latency.
- Process working set, managed heap size, and allocation rate.
- Time in GC and pause duration.
- Memory-pressure shedding events and deactivation failures.

If activation churn is high but memory is healthy, increase the age for the affected type. If memory stays high, reduce retained application state, shorten selected ages, scale out, or enable memory-pressure shedding. Configure [server GC](configuring-garbage-collection.md) for the silo process.
