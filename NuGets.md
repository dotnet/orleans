---
layout: page
title: Orleans NuGet Packages
---
{% include JB/setup %}


## Orleans NuGet packages as of [v1.0.9](https://github.com/dotnet/orleans/releases/tag/v1.0.9)

There are 4 key NuGet packages you will need to use in most scenarios:

### Microsoft.Orleans.Templates.Interfaces <small>[nuget](http://www.nuget.org/packages/Microsoft.Orleans.Templates.Interfaces/)</small>

```
PM> Install-Package Microsoft.Orleans.Templates.Interfaces
```

Build support for grain interfaces projects. Add it to your grain interfaces project to enable code generation for grain interfaces.

### Microsoft.Orleans.Templates.Grains <small>[nuget](http://www.nuget.org/packages/Microsoft.Orleans.Templates.Grains/)</small>

```
PM> Install-Package Microsoft.Orleans.Templates.Grains
```

Build support for grain implementation projects. Add it to your grain implementation project to enable code generation for grain classes

### Microsoft.Orleans.Core <small>[nuget](http://www.nuget.org/packages/Microsoft.Orleans.Core/)</small>

```
PM> Install-Package Microsoft.Orleans.Core
```

Contains Orleans.dll, which defines most of Orleans public types and Orleans Client. Reference it for building libraries and client applications that use Orleans types but don't need any of the included providers.

### Microsoft.Orleans.OrleansRuntime <small>[nuget](http://www.nuget.org/packages/Microsoft.Orleans.OrleansRuntime/)</small>

```
PM> Install-Package Microsoft.Orleans.OrleansRuntime
```

Contains the silo runtime. Reference it to host a silo within your process.

---

## Bundled Client/Server Packages

If you don't care about fine granularity of the NuGet packages, the Client and Server packages include everything you may (or may not) need on the client or server side.


### Microsoft.Orleans.Server <small>[nuget](http://www.nuget.org/packages/Microsoft.Orleans.Server/)</small>

```
PM> Install-Package Microsoft.Orleans.Server
```

Includes everything you need to host a silo, in a process or in Azure Worker Role.



### Microsoft.Orleans.Client <small>[nuget](http://www.nuget.org/packages/Microsoft.Orleans.Client/)</small>

```
PM> Install-Package Microsoft.Orleans.Client
```

Includes everything you need for an Orleans client (frontend), in a process or in Azure Web Role.

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
Contains Orleans dependecies on Azure SDK libraries, such as Azure Storage. Included in Microsoft.Orleans.OrleansProviders, Microsoft.Orleans.Client, and Microsoft.Orleans.Server.


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

