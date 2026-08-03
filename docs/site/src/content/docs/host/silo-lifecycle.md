---
title: Orleans silo lifecycle
description: Participate in Orleans silo startup and shutdown.
ms.date: 08/02/2026
ms.topic: concept-article
---

# Orleans silo lifecycle

Orleans starts and stops runtime components through an ordered observable lifecycle. Startup advances from the lowest stage to the highest. Shutdown runs the same stages in reverse.

Most application code should use .NET `IHostedService` or `BackgroundService`. Implement `ILifecycleParticipant<ISiloLifecycle>` only when a component must run at a precise point inside the Orleans runtime lifecycle.

## Lifecycle stages

<xref:Orleans.ServiceLifecycleStage> defines these stages:

| Stage | Value | Purpose |
|---|---:|---|
| `First` | `int.MinValue` | Earliest lifecycle stage |
| `RuntimeInitialize` | `2000` | Initialize the runtime |
| `RuntimeServices` | `4000` | Start core networking and runtime services |
| `RuntimeStorageServices` | `6000` | Initialize runtime storage services |
| `RuntimeGrainServices` | `8000` | Start grain-facing runtime services |
| `AfterRuntimeGrainServices` | `8100` | Run after grain runtime services |
| `ApplicationServices` | `10000` | Start application-level services |
| `ValidateInitialConnectivity` | `19900` | Validate connectivity before becoming active |
| `GrainDirectoryShutdown` | `19997` | Coordinate grain-directory shutdown |
| `GrainDeactivation` | `19998` | Deactivate grains during shutdown |
| `BecomeActive` | `19999` | Internal transition immediately before active |
| `Active` | `20000` | Silo is active and accepts workload |
| `Last` | `int.MaxValue` | Final lifecycle stage |

Some constants intentionally share or closely bracket values because their startup and shutdown semantics differ. Treat the named constants as ordering contracts; don't copy their numeric values into application code.

> [!IMPORTANT]
> `BecomeActive` is reserved for the membership and gateway transition. Application components normally use `ApplicationServices` or `Active`.

## Participate in the lifecycle

Register a singleton that implements `ILifecycleParticipant<ISiloLifecycle>`:

:::code language="csharp" source="snippets/hosting/HostingExamples.cs" id="lifecycle_participant":::

```csharp
siloBuilder.Services.AddSingleton<CacheLifecycleParticipant>();
siloBuilder.Services.AddSingleton<
    ILifecycleParticipant<ISiloLifecycle>>(
    services => services.GetRequiredService<CacheLifecycleParticipant>());
```

The start callback must complete before Orleans advances to the next stage. On shutdown, the stop callback receives the host shutdown cancellation token.

`ISiloLifecycle.HighestCompletedStage` and `LowestStoppedStage` expose lifecycle progress for diagnostics.

## Choose a stage

- Use `ApplicationServices` for dependencies that must start after core Orleans services and stop before them.
- Use `Active` when work requires an active silo and grain calls.
- Avoid runtime stages unless implementing infrastructure that depends on a specific Orleans subsystem.
- Don't subscribe application code at `BecomeActive`, `GrainDeactivation`, or `GrainDirectoryShutdown`.

Fail startup when a required dependency can't initialize. For optional or continuously retrying work, start a hosted background service after Orleans instead of blocking a lifecycle stage indefinitely.

## Diagnostics

The `Orleans.Runtime.SiloLifecycleSubject` logger reports participants, timing, and errors by stage. Enable `Information` logs while diagnosing startup ordering or slow shutdown. Lifecycle callbacks should log the external operation they are waiting for and honor cancellation.

For a simpler one-time callback, see [Background services and startup tasks](configuration-guide/startup-tasks.md). For host termination behavior, see [Shut down Orleans silos](configuration-guide/shutting-down-orleans.md).
