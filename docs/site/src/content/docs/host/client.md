---
title: Orleans clients
description: Choose and host an Orleans client.
ms.date: 08/02/2026
ms.topic: concept-article
---

# Orleans clients

An Orleans client lets application code call grains, use streams, and access other cluster services. Orleans provides a client automatically inside every silo, or you can host an external client in a separate process.

## Choose a hosting model

| Model | Prefer it when | Tradeoff |
|---|---|---|
| Co-hosted client | HTTP endpoints, background workers, and grains can share a process. | Simplest topology and fastest calls, but client workload shares silo CPU and memory. |
| External client | Frontends and silos need independent scaling, deployment, security, or resource isolation. | Adds gateways, network hops, and another process to operate. |

Start with a co-hosted client unless isolation is a requirement.

## Co-hosted clients

<a id="obtain-a-client-from-a-host"></a>

<xref:Microsoft.Extensions.Hosting.OrleansSiloGenericHostExtensions.UseOrleans*> registers <xref:Orleans.IClusterClient> and <xref:Orleans.IGrainFactory> in the host service provider:

:::code language="csharp" source="snippets/hosting/HostingExamples.cs" id="local_silo_and_client":::

Calls from a co-hosted client use the silo's cluster knowledge and don't require a gateway. If the target activation is local, Orleans can also avoid a network hop.

## External clients

<a id="initialize-a-grain-client"></a>
<a id="example"></a>

Install [Microsoft.Orleans.Client](https://www.nuget.org/packages/Microsoft.Orleans.Client), then add the client to the [.NET Generic Host](https://learn.microsoft.com/dotnet/core/extensions/generic-host) with <xref:Microsoft.Extensions.Hosting.OrleansClientGenericHostExtensions.UseOrleansClient*>:

:::code language="csharp" source="snippets/hosting/HostingExamples.cs" id="external_client":::

The client connects during host startup and is available from [.NET dependency injection](https://learn.microsoft.com/dotnet/core/extensions/dependency-injection) afterward. Register hosted services that use Orleans after <xref:Microsoft.Extensions.Hosting.OrleansClientGenericHostExtensions.UseOrleansClient*> so the host starts them after the client:

:::code language="csharp" source="snippets/ClusterClientHostedService.cs" id="cluster_client_hosted_service":::

See [Client configuration](configuration-guide/client-configuration.md) for clustering and gateway settings.

## Client connectivity

Orleans includes a default connection retry filter. During initial startup it retries eligible connection failures with linear backoff, up to 15 retries. If those retries are exhausted, the host fails to start instead of appearing healthy without a cluster connection.

You can replace the default with <xref:Orleans.Hosting.ClientBuilderExtensions.UseConnectionRetryFilter*> or <xref:Orleans.IClientConnectionRetryFilter>. Custom policies should:

- Retry only transient failures.
- Apply a finite attempt or time limit.
- Honor the supplied cancellation token.
- Let startup fail when the cluster or configuration is persistently unavailable.

### Client lifetime and registration

For generic-host and ASP.NET Core applications, treat <xref:Orleans.IClusterClient> as a singleton for the lifetime of the application process. Register it once with <xref:Microsoft.Extensions.Hosting.OrleansClientGenericHostExtensions.UseOrleansClient*> and resolve it from dependency injection instead of creating a second client per request, per controller, or in a static field.

A web app or worker can safely share the same client across all requests and background services, because Orleans is designed for concurrent use from multiple threads. The client is thread-safe for reuse; protect only mutable application state that you share outside Orleans.

Let the host own client startup and shutdown. The host starts the client during application startup and closes it during normal termination, so you should not dispose the dependency-injected singleton manually. If the cluster is unavailable during startup, the host fails fast rather than leaving the app in a partially started state.

After startup, Orleans refreshes gateways and reconnects as cluster membership changes. If a gateway or silo becomes unavailable, the client tries to reconnect automatically and a transient failure can still surface from an individual grain call. The grain reference remains valid, but retry the operation only if the application can safely tolerate duplicate execution.

## Make calls to grains

External client code isn't governed by the grain turn-based concurrency model. Multiple threads can use <xref:Orleans.IClusterClient> and grain references concurrently. Protect mutable client-side state using normal .NET synchronization.

Grain calls return <xref:System.Threading.Tasks.Task>, <xref:System.Threading.Tasks.Task`1>, <xref:System.Threading.Tasks.ValueTask>, or <xref:System.Threading.Tasks.ValueTask`1> according to the [grain interface rules](../grains/index.md). Always await calls rather than blocking threads.

## Receive notifications

Use [grain observers](../grains/observers.md) for best-effort, one-way callbacks to client objects. Add application-level acknowledgement or recovery when delivery matters. Use [streams](../streaming/index.md) when the stream provider's subscription and delivery model better fits the workflow.

## Dependency injection

Let the Generic Host own client startup and shutdown. Don't create a client per request, cache a second client in a static field, or dispose the dependency-injected singleton. When the host receives a termination signal, it closes the client as part of normal shutdown.
