---
layout: page
title: Orleans NuGet Packages
---
{% include JB/setup %}


### Orleans NuGet packages as of [v1.0.9](https://github.com/dotnet/orleans/releases/tag/v1.0.9)

There are 4 key NuGet packages you will need to use in most scenarios:

Package   | Purpose
------------- | -------------
[Microsoft.Orleans.Templates.Interfaces](http://www.nuget.org/packages/Microsoft.Orleans.Templates.Interfaces/) | Build support for grain interfaces projects. Add it to your grain interfaces project to enable code generation for grain interfaces.
[<br>Microsoft.Orleans.Templates.Grains](http://www.nuget.org/packages/Microsoft.Orleans.Templates.Grains/) | <br>Build support for grain implementation projects. Add it to your grain implementation project to enable code generation for grain classes.
[Microsoft.Orleans.Core](http://www.nuget.org/packages/Microsoft.Orleans.Core/) | Contains Orleans.dll, which defines most of Orleans public types and Orleans Client. Reference it for building libraries and client applications that use Orleans types but don't need any of the included providers.
[Microsoft.Orleans.OrleansRuntime](http://www.nuget.org/packages/Microsoft.Orleans.OrleansRuntime/) | <br>Contains the silo runtime. Reference it to host a silo within your process.
<br>
If you don't care about fine granularity of the NuGet packages, the Client and Server packages include everything you may (or may not) need on the client or server side.

Package   | Purpose
------------- | -------------
[Microsoft.Orleans.Server](http://www.nuget.org/packages/Microsoft.Orleans.Server/) | <br>Includes everything you need to host a silo, in a process or in Azure Worker Role.
[Microsoft.Orleans.Client](http://www.nuget.org/packages/Microsoft.Orleans.Client/) | <br>Includes everything you need for an Orleans client (frontend), in a process or in Azure Web Role.

<br>

The below aditional packages provide additional functionality.

Package   | Purpose
------------- | -------------
[<br>Microsoft.Orleans.OrleansHost](http://www.nuget.org/packages/Microsoft.Orleans.OrleansHost/) | <br>Includes a default silo host - OrleansHost.exe. Can be used for on-premises deployments or as an out-of-process silo host in Azure Worker Role. Included in Microsoft.Orleans.Server.
[Microsoft.Orleans.OrleansAzureUtils](http://www.nuget.org/packages/Microsoft.Orleans.OrleansAzureUtils/) | <br> Contains Orleans dependecies on Azure SDK libraries, such as Azure Runtime. Included in Microsoft.Orleans.Client and Microsoft.Orleans.Server.
[Microsoft.Orleans.OrleansProviders](http://www.nuget.org/packages/Microsoft.Orleans.OrleansProviders/) | <br>Contains a set of built-in persistence and stream providers. Included in Microsoft.Orleans.Client and Microsoft.Orleans.Server.
[Microsoft.Orleans.CounterControl](http://www.nuget.org/packages/Microsoft.Orleans.CounterControl/) | <br>Includes CounterControl.exe, which registers Windows performance counter categories for Orleans statistics and for deployed grain classes. Requires elevation. Can be executed in Azure as part of a role startup task. Included in Microsoft.Orleans.Server.
[Microsoft.Orleans.OrleansManager](http://www.nuget.org/packages/Microsoft.Orleans.OrleansManager/) | <br>Includes Orleans management tool - OrleansManager.exe. Not included in any other package.

