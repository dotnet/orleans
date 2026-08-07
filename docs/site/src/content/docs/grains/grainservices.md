---
title: Grain services
description: Implement silo-resident partitioned services for Orleans grains.
ms.date: 08/07/2026
ms.topic: how-to
---

# Grain services

A grain service is a silo-resident, remotely callable service that supports grain functionality. Orleans starts one instance on every silo and keeps it for the silo lifetime. A `GrainServiceClient<T>` maps a calling grain to the service instance responsible for it.

Reminders are an example of this pattern. Most application workloads should use regular grains or hosted services; use grain services when a framework-level capability must be partitioned across every silo.

## Define and implement the service

The service interface derives from <xref:Orleans.Services.IGrainService>:

```csharp
public interface IIndexService : IGrainService
{
    Task Add(string key);
}
```

Derive the implementation from <xref:Orleans.Runtime.GrainService>:

```csharp
[Reentrant]
public sealed class IndexService :
    GrainService,
    IIndexService
{
    public IndexService(
        GrainId id,
        Silo silo,
        ILoggerFactory loggerFactory)
        : base(id, silo, loggerFactory)
    {
    }

    public Task Add(string key)
    {
        return Task.CompletedTask;
    }
}
```

Override `Init`, `Start`, `StartInBackground`, and `Stop` only when the service needs work at those lifecycle points. Grain services aren't ordinary grains: they don't have a stable application identity, aren't collected when idle, and don't migrate.

## Create the grain-facing client

Define a client interface and proxy:

```csharp
public interface IIndexServiceClient :
    IGrainServiceClient<IIndexService>
{
    Task Add(string key);
}

public sealed class IndexServiceClient :
    GrainServiceClient<IIndexService>,
    IIndexServiceClient
{
    public IndexServiceClient(IServiceProvider services)
        : base(services)
    {
    }

    public Task Add(string key)
    {
        IIndexService service =
            GetGrainService(CurrentGrainReference.GrainId);

        return service.Add(key);
    }
}
```

`GetGrainService(GrainId)` consistently maps the calling grain to a service partition. Other overloads support explicit silo or hash routing for advanced implementations.

## Register and consume the service

Register both the service and its client:

```csharp
siloBuilder.AddGrainService<IndexService>();
siloBuilder.Services.AddSingleton<
    IIndexServiceClient,
    IndexServiceClient>();
```

Inject the client into a normal grain:

```csharp
public sealed class DocumentGrain(
    IIndexServiceClient indexService) :
    Grain,
    IDocumentGrain
{
    public Task Index()
    {
        return indexService.Add(this.GetPrimaryKeyString());
    }
}
```

The client can route to a remote silo; don't assume calls stay local.

Grain service implementation APIs are provided by [Microsoft.Orleans.Runtime](https://www.nuget.org/packages/Microsoft.Orleans.Runtime), which silo applications receive through `Microsoft.Orleans.Server`.

For a compiled implementation used by Orleans tests, see [`TestGrainService.cs`](https://github.com/dotnet/orleans/blob/main/test/Orleans.Runtime.Tests/GrainServiceTests/TestGrainService.cs).
