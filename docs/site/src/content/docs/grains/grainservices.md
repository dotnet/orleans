---
title: Grain services
description: Implement silo-resident partitioned services for Orleans grains.
ms.date: 08/12/2026
ms.topic: how-to
---

# Grain services

A grain service is a silo-resident, remotely callable service that supports grain functionality. Orleans starts one instance on every silo and keeps it for the silo lifetime. A <xref:Orleans.Runtime.Services.GrainServiceClient`1> maps a calling grain to the service instance responsible for it.

Reminders are an example of this pattern. Most application workloads should use regular grains or hosted services; use grain services when a framework-level capability must be partitioned across every silo.

## Define and implement the service

The service interface derives from <xref:Orleans.Services.IGrainService>:

:::code language="csharp" source="../snippets/compiled/Grains/ServicesAndObserversSnippets.cs" id="index_grain_service_interface":::
Derive the implementation from <xref:Orleans.Runtime.GrainService>:

:::code language="csharp" source="../snippets/compiled/Grains/ServicesAndObserversSnippets.cs" id="index_grain_service":::
Override <xref:Orleans.Runtime.GrainService.Init*>, <xref:Orleans.Runtime.GrainService.Start*>, <xref:Orleans.Runtime.GrainService.StartInBackground*>, and <xref:Orleans.Runtime.GrainService.Stop*> only when the service needs work at those lifecycle points. Grain services aren't ordinary grains: they don't have a stable application identity, aren't collected when idle, and don't migrate.

## Create the grain-facing client

Define a client interface and proxy:

:::code language="csharp" source="../snippets/compiled/Grains/ServicesAndObserversSnippets.cs" id="index_grain_service_client":::
<xref:Orleans.Runtime.Services.GrainServiceClient`1.GetGrainService*> consistently maps the calling grain to a service partition. Other overloads support explicit silo or hash routing for advanced implementations.

`IIndexService` is the runtime contract implemented by the silo-resident service. `IIndexServiceClient` is the grain-facing dependency-injection contract implemented by the proxy. Keep these roles separate: application grains call the client, and the client routes each call to an `IIndexService` instance.

## Register and consume the service

Register both the service and its client:

:::code language="csharp" source="../snippets/compiled/Grains/ServicesAndObserversSnippets.cs" id="register_index_grain_service":::
Inject the client into a normal grain:

:::code language="csharp" source="../snippets/compiled/Grains/ServicesAndObserversSnippets.cs" id="use_index_grain_service":::
Register and inject the same client interface. In this example, both registration and constructor injection use `IIndexServiceClient`. Registering `IIndexServiceClient` but requesting `IIndexService` in the grain constructor fails dependency resolution because those are distinct service types.

The client can route to a remote silo; don't assume calls stay local.

Grain service implementation APIs are provided by [Microsoft.Orleans.Runtime](https://www.nuget.org/packages/Microsoft.Orleans.Runtime), which silo applications receive through `Microsoft.Orleans.Server`.

For a compiled implementation used by Orleans tests, see [`TestGrainService.cs`](https://github.com/dotnet/orleans/blob/main/test/Orleans.Runtime.Tests/GrainServiceTests/TestGrainService.cs).
