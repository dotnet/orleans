---
title: Client configuration
description: Configure an Orleans external client.
ms.date: 08/02/2026
ms.topic: how-to
---

# Client configuration

An external client runs outside a silo process and reaches the cluster through silo gateways. Install [Microsoft.Orleans.Client](https://www.nuget.org/packages/Microsoft.Orleans.Client), call <xref:Microsoft.Extensions.Hosting.OrleansClientGenericHostExtensions.UseOrleansClient*>, and configure the same cluster identity and clustering provider as the silos:

:::code language="csharp" source="../snippets/hosting/HostingExamples.cs" id="external_client":::

Client settings participate in the [.NET options pattern](https://learn.microsoft.com/dotnet/core/extensions/options). The host starts Orleans before later registered hosted services and stops it with the rest of the application. Resolve <xref:Orleans.IClusterClient> or <xref:Orleans.IGrainFactory> from [.NET dependency injection](https://learn.microsoft.com/dotnet/core/extensions/dependency-injection); don't build a second client singleton manually.

## Orleans clustering information

- <xref:Orleans.Configuration.ClusterOptions.ServiceId> identifies the logical Orleans application and should remain stable.
- <xref:Orleans.Configuration.ClusterOptions.ClusterId> identifies one deployment of that service. Use a different value to isolate environments or parallel deployments.
- The client clustering provider discovers gateway-enabled silos. Its settings must point to the same membership data as the silos.

### Clustering provider

Common production [clustering packages](server-configuration.md#clustering-provider) include Azure Table Storage, ADO.NET, Redis, Azure Cosmos DB, DynamoDB, Consul, and ZooKeeper. Static and localhost clustering are intended for development. Kubernetes hosting is a silo integration, not a client clustering provider.

When Aspire supplies the Orleans resource, register the corresponding keyed service client and use the parameterless form:

:::code language="csharp" source="../snippets/aspire/Client/ClientProgram.cs" id="client_basic_config":::

## Connection resiliency

Orleans registers a default <xref:Orleans.IClientConnectionRetryFilter>. It retries eligible initial connection failures with linear backoff, up to 15 retries. Host startup fails if the client still can't connect, the host is stopping, or the failure isn't considered retryable.

Override the policy only when the application has different startup requirements:

:::code language="csharp" source="../snippets/hosting/HostingExamples.cs" id="client_retry":::

Bound every custom retry policy and honor the cancellation token so deployments can fail fast and shutdown isn't delayed indefinitely.

Initial connection retries don't make grain calls idempotent. A call can fail after the target started processing it, so retry application operations only when their semantics tolerate duplicates. Grain references remain usable after transient connectivity failures.

## Gateway behavior

Configure gateway refresh and connection behavior through <xref:Orleans.Configuration.GatewayOptions> or `Orleans:Gateway`. Orleans refreshes the gateway list from the clustering provider and reconnects as gateways become unavailable. Expose gateway endpoints only to client networks that require them; silo-to-silo traffic uses a separate endpoint.

For a co-hosted client, use <xref:Microsoft.Extensions.Hosting.OrleansSiloGenericHostExtensions.UseOrleans*> instead. The silo's client communicates directly with the cluster and doesn't require a gateway hop.
