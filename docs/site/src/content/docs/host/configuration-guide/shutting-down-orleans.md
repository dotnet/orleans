---
title: Shut down Orleans silos
description: Gracefully stop Orleans silos with the .NET Generic Host.
ms.date: 08/02/2026
ms.topic: how-to
---

# Shut down Orleans silos

Orleans is an <xref:Microsoft.Extensions.Hosting.IHostedService> inside the [.NET Generic Host](https://learn.microsoft.com/dotnet/core/extensions/generic-host). When the host stops, it calls Orleans' <xref:Microsoft.Extensions.Hosting.IHostedService.StopAsync*> method. Orleans then leaves the cluster, closes gateways and networking, deactivates grains, and stops providers in reverse lifecycle order.

## Graceful silo shutdown

For a standalone console process, use <xref:Microsoft.Extensions.Hosting.HostingHostBuilderExtensions.RunConsoleAsync*>. It installs the console lifetime, which requests host shutdown when the process receives a supported console interrupt or termination event:

:::code language="csharp" source="../snippets/hosting/HostingExamples.cs" id="run_silo":::

On Unix-like systems, container orchestrators normally send `SIGTERM`, and interactive terminals send `SIGINT` for <kbd>Ctrl</kbd>+<kbd>C</kbd>. Windows does not use POSIX signals; a Windows service or another process manager must use its host-lifetime integration to request shutdown. Web applications and service-managed applications should let their configured host lifetime initiate the same Generic Host stop sequence rather than adding a separate process-exit handler.

<xref:Microsoft.Extensions.Hosting.HostingAbstractionsHostExtensions.RunAsync*> starts a host and waits for it to stop, but it does not itself configure signal handling. Use it only when the application has already configured the appropriate host lifetime. For details, see [.NET Generic Host shutdown](https://learn.microsoft.com/dotnet/core/extensions/generic-host#host-shutdown).

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
