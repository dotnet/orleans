---
title: Activation collection and resource management
description: Manage idle Orleans activations and memory pressure.
ms.date: 08/08/2026
ms.topic: concept-article
---

# Activation collection and resource management

A grain activation is the in-memory instance that currently represents a grain identity. Orleans creates activations on demand and deactivates idle ones so a silo doesn't retain every grain it has ever served.

Activation collection is separate from .NET garbage collection. Orleans first deactivates the grain and removes runtime references; .NET GC can then reclaim the managed objects.

## How activation collection works

An activation becomes eligible for collection after it has been idle for <xref:Orleans.Configuration.GrainCollectionOptions.CollectionAge>. The default is 15 minutes. Orleans scans on <xref:Orleans.Configuration.GrainCollectionOptions.CollectionQuantum>, which defaults to one minute, so deactivation isn't scheduled at an exact instant.

Incoming grain calls, reminders, and stream events count as activity. Outgoing calls and arbitrary application I/O don't keep an activation active for collection purposes. Timers don't keep an activation active by default, but a timer created with <xref:Orleans.Runtime.GrainTimerCreationOptions.KeepAlive> resets activation idleness after each callback.

## Configuration

Configure a global age and targeted overrides:

:::code language="csharp" source="../snippets/hosting/HostingExamples.cs" id="activation_collection":::

Prefer type-specific changes over a cluster-wide increase. Longer ages trade memory for fewer activation and state-load operations.

## Explicit control of activation collection

### Expedite activation collection

Call <xref:Orleans.Grain.DeactivateOnIdle*> when the current activation should request deactivation after its current turn:

:::code language="csharp" source="../snippets/hosting/HostingExamples.cs" id="deactivate_on_idle":::

This instance method is only called from inside the grain implementation. It requests deactivation; it does not wait for the activation to stop. Queued calls are forwarded to a new or existing activation.

### Delay activation collection

Call <xref:Orleans.Grain.DelayDeactivation*> to keep an activation from idle collection for at least a period:

:::code language="csharp" source="../snippets/hosting/HostingExamples.cs" id="delay_deactivation":::

The delay combines with the configured collection age; it doesn't replace it. An activation becomes eligible for idle collection only after both the delay has elapsed and the activation has been idle for its collection age. Collection occurs on a later scan, so these times are lower bounds rather than schedules.

Each call sets a new deadline from the time of the call:

| Argument | Effect |
|---|---|
| A positive duration | Prevent idle collection until at least that duration has elapsed. |
| <xref:System.TimeSpan.Zero> | Cancel the previous delay and return to the configured collection age. |
| <xref:System.Threading.Timeout.InfiniteTimeSpan> or <xref:System.TimeSpan.MaxValue> | Delay idle collection indefinitely. |

For example, assume a 10-minute collection age and ignore scan latency:

| Activity | Earliest eligibility |
|---|---|
| Call `DelayDeactivation` with 20 minutes at minute 0, then make no calls. | Minute 20. |
| Call `DelayDeactivation` with 5 minutes at minute 0, then make no calls. | Minute 10, because a delay can't shorten the configured collection age. |
| Call `DelayDeactivation` with 20 minutes at minute 0, then receive an ordinary grain call at minute 7. | Minute 20. The delay doesn't slide when other calls arrive. |
| Call `DelayDeactivation` with 5 minutes at minute 0, then receive an ordinary grain call at minute 7. | No earlier than minute 17, after the new 10-minute idle period. |

<xref:Orleans.Grain.DeactivateOnIdle*> takes priority over a delay. Delaying deactivation is an optimization, not a durability or placement guarantee. It doesn't pin an activation to a silo, and failures, shutdown, migration, explicit deactivation, and memory pressure can still remove the activation.

### How to deactivate a specific grain identity

Code that needs to target a specific grain identity from outside the grain can cast the reference to the public <xref:Orleans.Core.Internal.IGrainManagementExtension> extension and call <xref:Orleans.Core.Internal.IGrainManagementExtension.DeactivateOnIdle*>:

:::code language="csharp" source="../snippets/hosting/HostingExamples.cs" id="deactivate_grain_externally":::

This is a request, not a wait-for-deactivation call. The method returns immediately after requesting deactivation for that grain identity; queued calls are forwarded to a new or existing activation. If there is no current activation, a later call can reactivate the grain.

The test-host helpers are convenience APIs for tests:

- <xref:Orleans.TestingHost.TestCluster.DeactivateAsync*> uses the public management extension and then waits for the server to finish deactivating the current activation.
- <xref:Orleans.TestingHost.InProcessTestCluster.DeactivateAsync*> directly deactivates the current grain context and waits for that deactivation to complete.

These helpers are valuable because they make deactivation deterministic in tests, but they are not a different runtime capability.

The <xref:Orleans.Grain.DeactivateOnIdle*> method is also available inside grain code when the grain itself decides that its current activation should end:

:::code language="csharp" source="../snippets/hosting/HostingExamples.cs" id="explicit_deactivate_grain":::

Use explicit deactivation only when there is a domain or operational reason to end an activation (for example, after completing a one-off workflow or releasing costly in-memory resources). Don't use it for ordinary lifecycle management; idle collection is the default and preferred behavior.

### What "idle" means for collection

An activation is idle when it hasn't processed inbound work during the configured idle window. Inbound grain calls, reminders, and stream events reset idleness. Outbound calls and arbitrary local work don't. Timer callbacks reset idleness only when the timer is created with <xref:Orleans.Runtime.GrainTimerCreationOptions.KeepAlive>.

`DeactivateOnIdle` requests deactivation when the current turn ends; queued calls are forwarded to a new or existing activation. A later call to the same grain identity can reactivate it on any compatible silo.

### Cautions

- Treat deactivation as best effort cleanup. It can be skipped by abrupt process termination and some failure paths.
- Persist important state as part of normal operations, not only in <xref:Orleans.Grain.OnDeactivateAsync*>.
- Don't use explicit deactivation as a substitute for memory-pressure tuning. Use collection settings and scaling based on measurements.

### Keep alive

Apply <xref:Orleans.KeepAliveAttribute> to a grain implementation only when the activation should be exempt from normal idle collection:

:::code language="csharp" source="../snippets/hosting/HostingExamples.cs" id="keep_alive_grain":::

Keep-alive activations still consume memory and aren't a substitute for durable state.

## Memory-based activation shedding

<a id="enable-memory-based-activation-shedding"></a>

Orleans can shed activations when process memory exceeds a configured percentage:

The defaults are:

### Configuration options

| Option | Default |
|---|---:|
| <xref:Orleans.Configuration.GrainCollectionOptions.EnableActivationSheddingOnMemoryPressure> | `false` |
| <xref:Orleans.Configuration.GrainCollectionOptions.MemoryUsageLimitPercentage> | `80` |
| <xref:Orleans.Configuration.GrainCollectionOptions.MemoryUsageTargetPercentage> | `75` |
| <xref:Orleans.Configuration.GrainCollectionOptions.MemoryUsagePollingPeriod> | 5 seconds |

### How it works

When enabled, Orleans estimates how many activations to deactivate to move from the limit toward the target, prioritizing older activations. Memory pressure can override normal keep-alive timing because protecting the process is more important than preserving an optimization.

Set container or process memory limits before tuning percentage thresholds. Leave enough space between target, limit, and the platform's hard limit for deactivation callbacks, state writes, and GC to complete.

## Activation and deactivation timeouts

<xref:Orleans.Configuration.GrainCollectionOptions.ActivationTimeout> and <xref:Orleans.Configuration.GrainCollectionOptions.DeactivationTimeout> both default to 30 seconds. These are runtime safety bounds, not goals. Keep <xref:Orleans.Grain.OnActivateAsync*> and <xref:Orleans.Grain.OnDeactivateAsync*>, state access, and dependency calls cancellable and normally much faster.

## Tune from measurements

Track at least:

- Activation count and activation/deactivation rate by grain type.
- State-load and state-write latency.
- Process working set, managed heap size, and allocation rate.
- Time in GC and pause duration.
- Memory-pressure shedding events and deactivation failures.

If activation churn is high but memory is healthy, increase the age for the affected type. If memory stays high, reduce retained application state, shorten selected ages, scale out, or enable memory-pressure shedding. Configure [server GC](configuring-garbage-collection.md) for the silo process.
