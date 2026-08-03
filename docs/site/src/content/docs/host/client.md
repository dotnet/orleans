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

## Use the co-hosted client

`UseOrleans` registers `IClusterClient` and `IGrainFactory` in the host service provider:

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.UseOrleans(siloBuilder =>
{
    // Configure the silo and its providers.
});

var app = builder.Build();

app.MapPost("/players/{id}/games/{gameId}",
    async (Guid id, Guid gameId, IClusterClient client) =>
    {
        var player = client.GetGrain<IPlayerGrain>(id);
        await player.JoinGame(gameId);
        return Results.Accepted();
    });

await app.RunAsync();
```

Calls from a co-hosted client use the silo's cluster knowledge and don't require a gateway. If the target activation is local, Orleans can also avoid a network hop.

## Use an external client

Install [Microsoft.Orleans.Client](https://www.nuget.org/packages/Microsoft.Orleans.Client), then add the client to the Generic Host:

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.UseOrleansClient(clientBuilder =>
{
    clientBuilder.Configure<ClusterOptions>(options =>
    {
        options.ServiceId = "game";
        options.ClusterId = "game-production";
    });

    // Configure the same clustering provider used by the silos.
});

var app = builder.Build();
await app.RunAsync();
```

The client connects during host startup and is available from dependency injection afterward. Register hosted services that use Orleans after `UseOrleansClient` so the host starts them after the client:

:::code language="csharp" source="snippets/ClusterClientHostedService.cs":::

See [Client configuration](configuration-guide/client-configuration.md) for clustering and gateway settings.

## Connection resiliency

Orleans includes a default connection retry filter. During initial startup it retries eligible connection failures with linear backoff, up to 15 retries. If those retries are exhausted, the host fails to start instead of appearing healthy without a cluster connection.

You can replace the default with `UseConnectionRetryFilter` or an `IClientConnectionRetryFilter`. Custom policies should:

- Retry only transient failures.
- Apply a finite attempt or time limit.
- Honor the supplied cancellation token.
- Let startup fail when the cluster or configuration is persistently unavailable.

After startup, Orleans refreshes gateways and reconnects as cluster membership changes. A transient failure can still surface from an individual grain call. The grain reference remains valid, but retry the operation only if the application can safely tolerate duplicate execution.

## Make concurrent calls

External client code isn't governed by the grain turn-based concurrency model. Multiple threads can use `IClusterClient` and grain references concurrently. Protect mutable client-side state using normal .NET synchronization.

Grain calls return `Task`, `Task<T>`, `ValueTask`, or `ValueTask<T>` according to the grain interface. Always await calls rather than blocking threads.

## Receive messages from grains

Use [grain observers](../grains/observers.md) for best-effort, one-way callbacks to client objects. Add application-level acknowledgement or recovery when delivery matters. Use [streams](../streaming/index.md) when the stream provider's subscription and delivery model better fits the workflow.

## Client lifetime

Let the Generic Host own client startup and shutdown. Don't create a client per request, cache a second client in a static field, or dispose the dependency-injected singleton. When the host receives a termination signal, it closes the client as part of normal shutdown.
