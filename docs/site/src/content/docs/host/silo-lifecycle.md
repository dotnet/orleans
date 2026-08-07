---
title: Orleans silo lifecycle
description: Participate in Orleans silo startup and shutdown.
ms.date: 08/02/2026
ms.topic: concept-article
---

# Orleans silo lifecycle

Orleans starts and stops runtime components through an ordered observable lifecycle. Startup advances from the lowest stage to the highest. Shutdown runs the same stages in reverse.

Most application code should use <xref:Microsoft.Extensions.Hosting.IHostedService> or <xref:Microsoft.Extensions.Hosting.BackgroundService>. Implement <xref:Orleans.ILifecycleParticipant`1> with <xref:Orleans.Runtime.ISiloLifecycle> only when a component must run at a precise point inside the Orleans runtime lifecycle.

## Stages

<xref:Orleans.ServiceLifecycleStage> defines these stages:

| Stage | Value | Purpose |
|---|---:|---|
| <xref:Orleans.ServiceLifecycleStage.First> | `int.MinValue` | Earliest lifecycle stage |
| <xref:Orleans.ServiceLifecycleStage.RuntimeInitialize> | `2000` | Initialize the runtime |
| <xref:Orleans.ServiceLifecycleStage.RuntimeServices> | `4000` | Start core networking and runtime services |
| <xref:Orleans.ServiceLifecycleStage.RuntimeStorageServices> | `6000` | Initialize runtime storage services |
| <xref:Orleans.ServiceLifecycleStage.RuntimeGrainServices> | `8000` | Start grain-facing runtime services |
| <xref:Orleans.ServiceLifecycleStage.AfterRuntimeGrainServices> | `8100` | Run after grain runtime services |
| <xref:Orleans.ServiceLifecycleStage.ApplicationServices> | `10000` | Start application-level services |
| <xref:Orleans.ServiceLifecycleStage.ValidateInitialConnectivity> | `19900` | Validate connectivity before becoming active |
| <xref:Orleans.ServiceLifecycleStage.GrainDirectoryShutdown> | `19997` | Coordinate grain-directory shutdown |
| <xref:Orleans.ServiceLifecycleStage.GrainDeactivation> | `19998` | Deactivate grains during shutdown |
| <xref:Orleans.ServiceLifecycleStage.BecomeActive> | `19999` | Internal transition immediately before active |
| <xref:Orleans.ServiceLifecycleStage.Active> | `20000` | Silo is active and accepts workload |
| <xref:Orleans.ServiceLifecycleStage.Last> | `int.MaxValue` | Final lifecycle stage |

Some constants intentionally share or closely bracket values because their startup and shutdown semantics differ. Treat the named constants as ordering contracts; don't copy their numeric values into application code.

> [!IMPORTANT]
> <xref:Orleans.ServiceLifecycleStage.BecomeActive> is reserved for the membership and gateway transition. Application components normally use <xref:Orleans.ServiceLifecycleStage.ApplicationServices> or <xref:Orleans.ServiceLifecycleStage.Active>.

## Silo lifecycle participation

Register a singleton that implements <xref:Orleans.ILifecycleParticipant`1> for <xref:Orleans.Runtime.ISiloLifecycle>:

<a id="example"></a>

:::code language="csharp" source="snippets/hosting/HostingExamples.cs" id="lifecycle_participant":::

:::code language="csharp" source="snippets/hosting/HostingExamples.cs" id="register_lifecycle_participant":::

The start callback must complete before Orleans advances to the next stage. On shutdown, the stop callback receives the host shutdown cancellation token.

<xref:Orleans.Runtime.ISiloLifecycle.HighestCompletedStage> and <xref:Orleans.Runtime.ISiloLifecycle.LowestStoppedStage> expose lifecycle progress for diagnostics.

## Choose a stage

- Use <xref:Orleans.ServiceLifecycleStage.ApplicationServices> for dependencies that must start after core Orleans services and stop before them.
- Use <xref:Orleans.ServiceLifecycleStage.Active> when work requires an active silo and grain calls.
- Avoid runtime stages unless implementing infrastructure that depends on a specific Orleans subsystem.
- Don't subscribe application code at <xref:Orleans.ServiceLifecycleStage.BecomeActive>, <xref:Orleans.ServiceLifecycleStage.GrainDeactivation>, or <xref:Orleans.ServiceLifecycleStage.GrainDirectoryShutdown>.

Fail startup when a required dependency can't initialize. For optional or continuously retrying work, start a hosted background service after Orleans instead of blocking a lifecycle stage indefinitely.

## Logging

The `Orleans.Runtime.SiloLifecycleSubject` logger category reports participants, timing, and errors by stage. Set <xref:Microsoft.Extensions.Logging.LogLevel> to `Information` while diagnosing startup ordering or slow shutdown. Lifecycle callbacks should log the external operation they are waiting for and honor cancellation.

For implementation details, see [Orleans lifecycle](../implementation/orleans-lifecycle.md). For a simpler one-time callback, see [Background services and startup tasks](configuration-guide/startup-tasks.md). For host termination behavior, see [Shut down Orleans silos](configuration-guide/shutting-down-orleans.md).
