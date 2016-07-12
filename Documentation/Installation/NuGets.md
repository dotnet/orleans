---
layout: page
title: Orleans NuGet Packages
---



## Orleans NuGet packages as of [v1.2.0](https://github.com/dotnet/orleans/releases/tag/v1.2.0)

There are 4 key NuGet packages you will need to use in most scenarios:

### [Microsoft.Orleans.OrleansCodeGenerator.Build](http://www.nuget.org/packages/Microsoft.Orleans.OrleansCodeGenerator.Build/)

```
PM> Install-Package Microsoft.Orleans.OrleansCodeGenerator.Build 
```

Build support for grain interfaces and implementation projects. Add it to your grain interfaces and implementation projects to enable code generation of grain references and serializers. `Microsoft.Orleans.Templates.Interfaces` and `Microsoft.Orleans.Templates.Grains` packages are obsolete and provided only for backward compatibility and migration.

### [Microsoft.Orleans.Core](http://www.nuget.org/packages/Microsoft.Orleans.Core/)

```
PM> Install-Package Microsoft.Orleans.Core
```

Contains Orleans.dll, which defines most of Orleans public types and Orleans Client. Reference it for building libraries and client applications that use Orleans types but don't need any of the included providers.

### [Microsoft.Orleans.Server](http://www.nuget.org/packages/Microsoft.Orleans.Server/)

```
PM> Install-Package Microsoft.Orleans.Server
```

Includes everything you need to run a silo.


### [Microsoft.Orleans.Client](http://www.nuget.org/packages/Microsoft.Orleans.Client/)

```
PM> Install-Package Microsoft.Orleans.Client
```

Includes everything you need for an Orleans client (frontend).

---

## Additional Packages

The below packages provide additional functionality.

### [Microsoft.Orleans.OrleansServiceBus](http://www.nuget.org/packages/Microsoft.Orleans.OrleansServiceBus/)

```
PM> Install-Package Microsoft.Orleans.OrleansServiceBus
```
Includes the stream provider for Azure Event Hubs.

### [Microsoft.Orleans.OrleansHost](http://www.nuget.org/packages/Microsoft.Orleans.OrleansHost/)

```
PM> Install-Package Microsoft.Orleans.OrleansHost
```
Includes the default silo host - OrleansHost.exe. Can be used for on-premises deployments or as an out-of-process silo host in Azure Worker Role. Included in Microsoft.Orleans.Server.

### [Microsoft.Orleans.OrleansAzureUtils](http://www.nuget.org/packages/Microsoft.Orleans.OrleansAzureUtils/)

```
PM> Install-Package Microsoft.Orleans.OrleansAzureUtils
```
Contains a wrapper class that simplifies instantiation of silos and clients in Azure Worker/Web roles, Azure Table based membership provider, and persistence and stream providers for Azure Storage.


### [Microsoft.Orleans.OrleansProviders](http://www.nuget.org/packages/Microsoft.Orleans.OrleansProviders/)

```
PM> Install-Package Microsoft.Orleans.OrleansProviders
```
Contains a set of built-in persistence and stream providers. Included in Microsoft.Orleans.Client and Microsoft.Orleans.Server.

### [Microsoft.Orleans.CounterControl](http://www.nuget.org/packages/Microsoft.Orleans.CounterControl/)

```
PM> Install-Package Microsoft.Orleans.CounterControl
```
Includes OrleansCounterControl.exe, which registers Windows performance counter categories for Orleans statistics and for deployed grain classes. Requires elevation. Can be executed in Azure as part of a role startup task. Included in Microsoft.Orleans.Server.

### [Microsoft.Orleans.OrleansManager](http://www.nuget.org/packages/Microsoft.Orleans.OrleansManager/)

```
PM> Install-Package Microsoft.Orleans.OrleansManager
```
Includes Orleans management tool - OrleansManager.exe.

### [Microsoft.Orleans.OrleansConsulUtils](http://www.nuget.org/packages/Microsoft.Orleans.OrleansConsulUtils/)

```
PM> Install-Package Microsoft.Orleans.OrleansConsulUtils
```
Includes the plugin for using Consul for storing cluster membership data.

### [Microsoft.Orleans.OrleansZooKeeperUtils](http://www.nuget.org/packages/Microsoft.Orleans.OrleansZooKeeperUtils/)

```
PM> Install-Package Microsoft.Orleans.OrleansZooKeeperUtils
```
Includes the plugin for using ZooKeeper for storing cluster membership data.

### [Microsoft.Orleans.TestingHost](http://www.nuget.org/packages/Microsoft.Orleans.TestingHost/)

```
PM> Install-Package Microsoft.Orleans.TestingHost
```
Includes the library for hosting silos in a testing project.

### [Microsoft.Orleans.OrleansCodeGenerator](http://www.nuget.org/packages/Microsoft.Orleans.OrleansCodeGenerator/)

```
PM> Install-Package Microsoft.Orleans.OrleansCodeGenerator
```
Includes the run time code generator. Included in Microsoft.Orleans.Server and Microsoft.Orleans.Client.

### [Microsoft.Orleans.OrleansTelemetryConsumers.AI](http://www.nuget.org/packages/Microsoft.Orleans.OrleansTelemetryConsumers.AI/)

```
PM> Install-Package Microsoft.Orleans.OrleansTelemetryConsumers.AI
```
Includes the telemetry consumer for Azure Application Insights.

### [Microsoft.Orleans.OrleansTelemetryConsumers.NewRelic](http://www.nuget.org/packages/Microsoft.Orleans.OrleansTelemetryConsumers.NewRelic/)

```
PM> Install-Package Microsoft.Orleans.OrleansTelemetryConsumers.NewRelic
```
Includes the telemetry consumer for NewRelic.

### [Microsoft.Orleans.Serialization.Bond](http://www.nuget.org/packages/Microsoft.Orleans.Serialization.Bond/)

```
PM> Install-Package Microsoft.Orleans.Serialization.Bond
```
Includes support for [Bond serializer](https://github.com/microsoft/bond).

