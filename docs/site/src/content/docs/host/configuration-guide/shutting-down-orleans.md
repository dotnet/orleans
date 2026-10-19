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
- Admitted client-observer invocations and their response handling.
- Stream, reminder, storage, and telemetry providers to flush and stop.

If the external grace period expires first, the process is killed and graceful shutdown can't complete.

## Make application code shutdown-safe

- Honor cancellation tokens in hosted services, startup tasks, lifecycle participants, and provider calls.
- Keep <xref:Orleans.Grain.OnDeactivateAsync*> bounded; persist important state during normal operation rather than relying only on shutdown.
- Stop accepting new application work before the termination deadline.
- Make recovery safe after abrupt termination, because crashes and node loss remain possible.
- Avoid synchronous blocking and unbounded retries in shutdown callbacks.

Grains can move or reactivate elsewhere after a silo leaves. Don't use graceful shutdown as an application-wide drain barrier unless the application separately coordinates that behavior.

### Queued requests during shutdown

When a silo shuts down gracefully, an activation which still has queued requests after its running requests finish migrates to another silo, carrying those requests in its migration package. The new activation queues them before it can receive any other request, registers in the grain directory in place of the old one, and processes them first. The calling silos then record the request's new location, so their calls keep waiting for a response after the original silo leaves the cluster. Calls retain their original identities and response timeouts. Every migration carries the activation's queued requests in the same way, including migrations requested with <xref:Orleans.Grain.MigrateOnIdle>.

Requests which can't migrate with their activation are forwarded instead. This includes requests for stateless workers and <xref:Orleans.Placement.ImmovableAttribute> grains, requests which together exceed <xref:Orleans.Configuration.MessagingOptions.MaxMessageBodySize>, requests which arrive after the activation's migration has begun, requests for an activation which was already deactivating for another reason when shutdown began, requests for a grain which is already active on the destination silo, and requests migrating to a silo which runs an earlier version. Callers of forwarded requests fail with <xref:Orleans.Runtime.SiloUnavailableException> if the original silo leaves the cluster before the response arrives, as they do when a silo crashes.

A migration counts as a forward. Requests which have already been forwarded <xref:Orleans.Configuration.SiloMessagingOptions.MaxForwardCount> times stay with the original activation and are rejected when it deactivates, so their callers fail with a rejection without the request executing.

Migration adds activation time on the destination silo to shutdown, in two phases. First, a migrating activation waits for the destination silo to accept the migration. Once the migration has been sent, the activation waits for that response, up to <xref:Orleans.Configuration.SiloMessagingOptions.SystemResponseTimeout>, since the destination may already have taken its requests. Second, after the destination takes the requests, the source retargets their callers' pending calls. When <xref:Orleans.Configuration.GrainCollectionOptions.DeactivationTimeout> elapses, a migration which has not been sent is canceled and its requests are forwarded, and a migration whose requests the destination has taken completes without waiting for the retargeting to finish. Allow the host shutdown budget enough time for both phases.

## Containers and orchestrators

Remove the instance from application traffic before requesting host shutdown. Then send the platform's normal termination request, allow the host shutdown budget to elapse, and reserve forceful termination for hung processes. A forced termination, power loss, or process crash can bypass the host entirely, so correctness must not depend on graceful shutdown.

## See also

For ordered Orleans callbacks, see [Orleans silo lifecycle](../silo-lifecycle.md).
