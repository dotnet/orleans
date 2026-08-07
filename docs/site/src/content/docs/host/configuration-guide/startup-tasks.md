---
title: Background services and startup tasks
description: Run application initialization and background work with Orleans.
ms.date: 08/02/2026
ms.topic: how-to
---

# Background services and startup tasks

Use standard [.NET hosted services](https://learn.microsoft.com/dotnet/core/extensions/workers) for application initialization and background work. Orleans participates in the same Generic Host, so registration order can ensure Orleans is ready before a hosted service starts.

<a id="using-backgroundservice-recommended"></a>

## Using <xref:Microsoft.Extensions.Hosting.BackgroundService> (Recommended)

Register Orleans first, then the <xref:Microsoft.Extensions.Hosting.BackgroundService>:

:::code language="csharp" source="../snippets/hosting/HostingExamples.cs" id="register_background_service":::

:::code language="csharp" source="../snippets/hosting/HostingExamples.cs" id="background_service":::

Honor `stoppingToken` so the service doesn't delay silo shutdown. Use <xref:Microsoft.Extensions.Hosting.IHostedService> directly for one-time startup and shutdown work.

## Orleans startup tasks

### Register a delegate

Use <xref:Orleans.Hosting.SiloBuilderStartupExtensions.AddStartupTask*> when initialization must complete at an Orleans lifecycle stage before startup can continue:

:::code language="csharp" source="../snippets/hosting/HostingExamples.cs" id="register_startup_task":::

The default stage is <xref:Orleans.ServiceLifecycleStage.Active>. An exception fails silo startup. This is appropriate for mandatory validation or initialization, but not for optional work that can retry after the host becomes ready.

<a id="register-an-istartuptask-implementation"></a>

### Register an <xref:Orleans.Runtime.IStartupTask> implementation

For reusable tasks, implement <xref:Orleans.Runtime.IStartupTask>:

:::code language="csharp" source="../snippets/hosting/HostingExamples.cs" id="validate_dependencies_task":::

:::code language="csharp" source="../snippets/hosting/HostingExamples.cs" id="register_validate_dependencies_task":::

## Choose the right mechanism

<a id="using-ihostedservice"></a>

| Requirement | Mechanism |
|---|---|
| Continuous loop or scheduled application work | <xref:Microsoft.Extensions.Hosting.BackgroundService> |
| One-time host startup and shutdown work | <xref:Microsoft.Extensions.Hosting.IHostedService> |
| Mandatory initialization at a specific Orleans stage | <xref:Orleans.Hosting.SiloBuilderStartupExtensions.AddStartupTask*> |
| Start and stop callbacks integrated with an Orleans subsystem | <xref:Orleans.ILifecycleParticipant`1> with <xref:Orleans.Runtime.ISiloLifecycle> |

Don't use a startup task for long-running loops, database migrations that multiple replicas could race to apply, or work that needs unbounded retries. Coordinate migrations externally or make them safely single-writer.

See [Orleans silo lifecycle](../silo-lifecycle.md) for lifecycle stage selection.
