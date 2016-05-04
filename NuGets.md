---
layout: page
title: Orleans NuGet Packages
---
{% include JB/setup %}


## Orleans NuGet packages as of [v1.2.0](https://github.com/dotnet/orleans/releases/tag/v1.2.0)

There are 4 key NuGet packages you will need to use in most scenarios:

### Microsoft.Orleans.OrleansCodeGenerator.Build <small>[nuget](http://www.nuget.org/packages/Microsoft.Orleans.OrleansCodeGenerator.Build/)</small>

```
PM> Install-Package Microsoft.Orleans.OrleansCodeGenerator.Build 
```

Build support for grain interfaces and implementation projects. Add it to your grain interfaces and implementation projects to enable code generation of grain references and serializers.

### Microsoft.Orleans.Core <small>[nuget](http://www.nuget.org/packages/Microsoft.Orleans.Core/)</small>

```
PM> Install-Package Microsoft.Orleans.Core
```

Contains Orleans.dll, which defines most of Orleans public types and Orleans Client. Reference it for building libraries and client applications that use Orleans types but don't need any of the included providers.

### Microsoft.Orleans.Server <small>[nuget](http://www.nuget.org/packages/Microsoft.Orleans.Server/)</small>

```
PM> Install-Package Microsoft.Orleans.Server
```

Includes everything you need to host a silo.


### Microsoft.Orleans.Client <small>[nuget](http://www.nuget.org/packages/Microsoft.Orleans.Client/)</small>

```
PM> Install-Package Microsoft.Orleans.Client
```

Includes everything you need for an Orleans client (frontend).

---

## Additional Packages

The below packages provide additional functionality.

### Microsoft.Orleans.OrleansHost <small>[nuget](http://www.nuget.org/packages/Microsoft.Orleans.OrleansHost/)</small>

```
PM> Install-Package Microsoft.Orleans.OrleansHost
```
Includes the default silo host - OrleansHost.exe. Can be used for on-premises deployments or as an out-of-process silo host in Azure Worker Role. Included in Microsoft.Orleans.Server.

### Microsoft.Orleans.OrleansAzureUtils <small>[nuget](http://www.nuget.org/packages/Microsoft.Orleans.OrleansAzureUtils/)</small>

```
PM> Install-Package Microsoft.Orleans.OrleansAzureUtils
```
Contains a wrapper class that simplifies instantiation of silos and clients in Azure Worker/Web roles, Azure Table based membership provider, and persistence and stream providers for Azure Storage.


### Microsoft.Orleans.OrleansProviders <small>[nuget](http://www.nuget.org/packages/Microsoft.Orleans.OrleansProviders/)</small>

```
PM> Install-Package Microsoft.Orleans.OrleansProviders
```
Contains a set of built-in persistence and stream providers. Included in Microsoft.Orleans.Client and Microsoft.Orleans.Server.

### Microsoft.Orleans.CounterControl <small>[nuget](http://www.nuget.org/packages/Microsoft.Orleans.CounterControl/)</small>

```
PM> Install-Package Microsoft.Orleans.CounterControl
```
Includes OrleansCounterControl.exe, which registers Windows performance counter categories for Orleans statistics and for deployed grain classes. Requires elevation. Can be executed in Azure as part of a role startup task. Included in Microsoft.Orleans.Server.

### Microsoft.Orleans.OrleansManager <small>[nuget](http://www.nuget.org/packages/Microsoft.Orleans.OrleansManager/)</small>

```
PM> Install-Package Microsoft.Orleans.OrleansManager
```
Includes Orleans management tool - OrleansManager.exe.

### Microsoft.Orleans.OrleansZooKeeperUtils <small>[nuget](http://www.nuget.org/packages/Microsoft.Orleans.OrleansZooKeeperUtils/)</small>

```
PM> Install-Package Microsoft.Orleans.OrleansZooKeeperUtils
```
Includes the plugin for using ZooKeeper for storing cluster membership data.

### Microsoft.Orleans.TestingHost <small>[nuget](http://www.nuget.org/packages/Microsoft.Orleans.TestingHost/)</small>

```
PM> Install-Package Microsoft.Orleans.TestingHost
```
Includes the library for hosting silos in a testing project.

