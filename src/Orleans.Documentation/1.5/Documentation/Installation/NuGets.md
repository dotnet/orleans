---
layout: page
title: Orleans NuGet Packages
---

[!include[](../../warning-banner.md)]

# Nuget Packages

## Orleans NuGet packages as of [v1.5.0](https://github.com/dotnet/orleans/releases/tag/v1.5.0)

There are 4 key NuGet packages you will need to use in most scenarios:

### [Microsoft Orleans Build-time Code Generation](https://www.nuget.org/packages/Microsoft.Orleans.OrleansCodeGenerator.Build/)

```
PM> Install-Package Microsoft.Orleans.OrleansCodeGenerator.Build
```

Build support for grain interfaces and implementation projects.
Add it to your grain interfaces and implementation projects to enable code generation of grain references and serializers.
`Microsoft.Orleans.Templates.Interfaces` and `Microsoft.Orleans.Templates.Grains` packages are obsolete and provided only for backward compatibility and migration.

### [Microsoft Orleans Core Library](https://www.nuget.org/packages/Microsoft.Orleans.Core/)

```
PM> Install-Package Microsoft.Orleans.Core
```

Contains Orleans.dll, which defines most of Orleans public types and Orleans Client.
Reference it for building libraries and client applications that use Orleans types but don't need any of the included providers.

### [Microsoft Orleans Server Libraries](https://www.nuget.org/packages/Microsoft.Orleans.Server/)

```
PM> Install-Package Microsoft.Orleans.Server
```

Includes everything you need to run a silo.


### [Microsoft Orleans Client Libraries](https://www.nuget.org/packages/Microsoft.Orleans.Client/)

```
PM> Install-Package Microsoft.Orleans.Client
```

Includes everything you need for an Orleans client (frontend).

---

## Additional Packages

The below packages provide additional functionality.

### [Microsoft Orleans Providers](https://www.nuget.org/packages/Microsoft.Orleans.OrleansProviders/)

```
PM> Install-Package Microsoft.Orleans.OrleansProviders
```
Contains a set of built-in persistence and stream providers, primarily for testing, as well as some abstractions and utility types for building persistence and stream providers.
Included in Microsoft.Orleans.Client and Microsoft.Orleans.Server.

### [Microsoft Orleans Event-Sourcing](https://www.nuget.org/packages/Microsoft.Orleans.EventSourcing/)

```
PM> Install-Package Microsoft.Orleans.EventSourcing 
```
Contains a set of base types for creating grain classes with event-sourced state.

## Providers and extensions

### [Microsoft Orleans Azure Utilities](https://www.nuget.org/packages/Microsoft.Orleans.OrleansAzureUtils/)

```
PM> Install-Package Microsoft.Orleans.OrleansAzureUtils
```
Contains Azure Table based cluster membership provider, wrapper classes that simplify instantiation of silos and clients in Azure Worker/Web roles, persistence providers for Azure Tables and Azure Blobs, and a stream provider for Azure Queues.


### [Microsoft Orleans Sql Utilities](https://www.nuget.org/packages/Microsoft.Orleans.OrleansSqlUtils/)

```
PM> Install-Package Microsoft.Orleans.OrleansSqlUtils 
```
Contains SQL based cluster membership and persistence providers for use with SQL Server, MySQL, PostgreSQL, and other SQL databases.


#### [Microsoft Orleans ServiceBus Utilities](https://www.nuget.org/packages/Microsoft.Orleans.OrleansServiceBus/)

```
PM> Install-Package Microsoft.Orleans.OrleansServiceBus
```
Includes the stream provider for Azure Event Hubs.

### [Microsoft Orleans Consul Utilities](https://www.nuget.org/packages/Microsoft.Orleans.OrleansConsulUtils/)

```
PM> Install-Package Microsoft.Orleans.OrleansConsulUtils
```
Includes the plugin for using Consul for storing cluster membership data.

### [Microsoft Orleans ZooKeeper Utilities](https://www.nuget.org/packages/Microsoft.Orleans.OrleansZooKeeperUtils/)

```
PM> Install-Package Microsoft.Orleans.OrleansZooKeeperUtils
```
Includes the plugin for using ZooKeeper for storing cluster membership data.

### [Microsoft Orleans AWS Utilities ](https://www.nuget.org/packages/Microsoft.Orleans.OrleansAWSUtils/)

```
PM> Install-Package Microsoft.Orleans.OrleansAWSUtils
```
Includes DynamoDB based cluster membership provider, DynamoDB persistence provider, and SQS based stream provider.

### [Microsoft Orleans Telemetry Consumer - Performance Counters](https://www.nuget.org/packages/Microsoft.Orleans.OrleansTelemetryConsumers.Counters/)

```
PM> Install-Package Microsoft.Orleans.OrleansTelemetryConsumers.Counters
```
Windows Performance Counters implementation of Orleans Telemetry API.

### [Microsoft Orleans Telemetry Consumer - Azure Application Insights](https://www.nuget.org/packages/Microsoft.Orleans.OrleansTelemetryConsumers.AI/)

```
PM> Install-Package Microsoft.Orleans.OrleansTelemetryConsumers.AI
```
Includes the telemetry consumer for Azure Application Insights.

### [Microsoft Orleans Telemetry Consumer - NewRelic](https://www.nuget.org/packages/Microsoft.Orleans.OrleansTelemetryConsumers.NewRelic/)

```
PM> Install-Package Microsoft.Orleans.OrleansTelemetryConsumers.NewRelic
```
Includes the telemetry consumer for NewRelic.

### [Microsoft Orleans Bond Serializer](https://www.nuget.org/packages/Microsoft.Orleans.Serialization.Bond/)

```
PM> Install-Package Microsoft.Orleans.Serialization.Bond
```
Includes support for [Bond serializer](https://github.com/microsoft/bond).

### [Microsoft Orleans Google Utilities ](https://www.nuget.org/packages/Microsoft.Orleans.OrleansGoogleUtils/)

```
PM> Install-Package Microsoft.Orleans.OrleansGoogleUtils
```
Includes Google Protocol Buffers serializer.

## Hosting and testing

### [Microsoft Orleans Runtime](https://www.nuget.org/packages/Microsoft.Orleans.OrleansRuntime/)

```
PM> Install-Package Microsoft.Orleans.OrleansRuntime 
```
Core runtime library of Microsoft Orleans that hosts and executes grains within a silo.

### [Microsoft Orleans Silo Host](https://www.nuget.org/packages/Microsoft.Orleans.OrleansHost/)

```
PM> Install-Package Microsoft.Orleans.OrleansHost
```
Includes the default silo host - OrleansHost.exe. Can be used for on-premises deployments or as an out-of-process silo host in Azure Worker Role. Included in Microsoft.Orleans.Server.
**We are planning to deprecate this package in favor of customers building their custom silo host processes in order to simplify dependency management and programmatic configuration.**

### [Microsoft Orleans Service Fabric Support](https://www.nuget.org/packages/Microsoft.Orleans.ServiceFabric/)

```
PM> Install-Package Microsoft.Orleans.ServiceFabric 
```
Support for hosting Microsoft Orleans on Service Fabric.

### [Microsoft Orleans Testing Host Library](https://www.nuget.org/packages/Microsoft.Orleans.TestingHost/)

```
PM> Install-Package Microsoft.Orleans.TestingHost
```
Includes the library for hosting silos in a testing project.

### [Microsoft Orleans Code Generation](https://www.nuget.org/packages/Microsoft.Orleans.OrleansCodeGenerator/)

```
PM> Install-Package Microsoft.Orleans.OrleansCodeGenerator
```
Includes the run time code generator. Included in Microsoft.Orleans.Server and Microsoft.Orleans.Client.

## Tools

### [Microsoft Orleans Performance Counter Tool](https://www.nuget.org/packages/Microsoft.Orleans.CounterControl/)

```
PM> Install-Package Microsoft.Orleans.CounterControl
```
Includes OrleansCounterControl.exe, which registers Windows performance counter categories for Orleans statistics and for deployed grain classes. Requires elevation. Can be executed in Azure as part of a role startup task. Included in Microsoft.Orleans.Server.

### [Microsoft Orleans Management Tool](https://www.nuget.org/packages/Microsoft.Orleans.OrleansManager/)

```
PM> Install-Package Microsoft.Orleans.OrleansManager
```
Includes Orleans management tool - OrleansManager.exe.
**We are planning to deprecate this package in favor of customers building their custom management tools in order to simplify dependency management and programmatic configuration.**
