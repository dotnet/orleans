---
title: Shut down Orleans silos
description: Gracefully stop Orleans silos with the .NET Generic Host.
ms.date: 08/02/2026
ms.topic: how-to
---

# Shut down Orleans silos

Orleans is an <xref:Microsoft.Extensions.Hosting.IHostedService> inside the [.NET Generic Host](https://learn.microsoft.com/dotnet/core/extensions/generic-host). When the host stops, it calls Orleans' <xref:Microsoft.Extensions.Hosting.IHostedService.StopAsync*> method. Orleans then leaves the cluster, closes gateways and networking, deactivates grains, and stops providers in reverse lifecycle order.

## Graceful silo shutdown

Build the host with <xref:Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder*> and run it with <xref:Microsoft.Extensions.Hosting.HostingAbstractionsHostExtensions.RunAsync*> or <xref:Microsoft.Extensions.Hosting.HostingAbstractionsHostExtensions.Run*>. Both `Host.CreateApplicationBuilder` and the older `Host.CreateDefaultBuilder` register <xref:Microsoft.Extensions.Hosting.IHostLifetime> with the console lifetime by default, so no additional call is needed to observe termination requests:

:::code language="csharp" source="../snippets/hosting/HostingExamples.cs" id="run_silo":::

The configured .NET host lifetime handles the relevant termination events and initiates the Generic Host shutdown sequence, so applications shouldn't add a separate process-exit handler. For details, see [.NET Generic Host shutdown](https://learn.microsoft.com/dotnet/core/extensions/generic-host#host-shutdown).

In tests or embedded hosts, call <xref:Microsoft.Extensions.Hosting.HostingAbstractionsHostExtensions.StopAsync*> and dispose the host:

:::code language="csharp" source="../snippets/hosting/HostingExamples.cs" id="stop_host":::

Don't call <xref:System.Environment.Exit*>, kill the process from application code, or dispose Orleans services independently of the host.

## Configure a shutdown budget

The host passes a cancellation token to every hosted service during shutdown, including Orleans. <xref:Microsoft.Extensions.Hosting.HostOptions.ShutdownTimeout> cancels that token after the configured budget. Configure it for the expected workload:

:::code language="csharp" source="../snippets/hosting/HostingExamples.cs" id="shutdown_timeout":::

Set the orchestrator's termination grace period longer than the host shutdown timeout. Include time for:

- Load balancers and readiness probes to stop sending new traffic.
- Gateway and membership changes to propagate.
- Grain deactivation callbacks and state writes.
- Stream, reminder, storage, and telemetry providers to flush and stop.

If the external grace period expires first, the process is killed and graceful shutdown can't complete.

## Make application code shutdown-safe

- Honor cancellation tokens in hosted services, startup tasks, lifecycle participants, and provider calls.
- Keep <xref:Orleans.Grain.OnDeactivateAsync*> bounded; persist important state during normal operation rather than relying only on shutdown.
- Stop accepting new application work before the termination deadline.
- Make recovery safe after abrupt termination, because crashes and node loss remain possible.
- Avoid synchronous blocking and unbounded retries in shutdown callbacks.

Grains can move or reactivate elsewhere after a silo leaves. Don't use graceful shutdown as an application-wide drain barrier unless the application separately coordinates that behavior.

## Containers and orchestrators

Remove the instance from application traffic before requesting host shutdown. Then send the platform's normal termination request, allow the host shutdown budget to elapse, and reserve forceful termination for hung processes. A forced termination, power loss, or process crash can bypass the host entirely, so correctness must not depend on graceful shutdown.

## See also

For ordered Orleans callbacks, see [Orleans silo lifecycle](../silo-lifecycle.md).
