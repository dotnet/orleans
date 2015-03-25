---
layout: page
title: Orleans NuGet Packages
---
{% include JB/setup %}


### Orleans NuGet packages as of v1.0.5


In v1.0.5 we made important chnages to the set of NuGet packages. With these changes, grain interface and grain class projects can now be created and built without Orleans SDK installed. We also retired two packages with this change.

There are four key NuGet packages you will use in most scenarios.

Package   | Purpose
------------- | -------------
[Microsoft.Orleans.Templates.Interfaces](http://www.nuget.org/packages/Microsoft.Orleans.Templates.Interfaces/) | Build support for grain interfaces projects. Add it to your project to enable code generation for grain interfaces.
[Microsoft.Orleans.Templates.Grains](http://www.nuget.org/packages/Microsoft.Orleans.Templates.Grains/) | Build support for grain implementation projects. Add it to your project to enable code generation for grain classes.
[Microsoft.Orleans.Server](http://www.nuget.org/packages/Microsoft.Orleans.Server/) | Includes everything you need to host a silo, in a process or an Azure Worker Role.
[Microsoft.Orleans.Client](http://www.nuget.org/packages/Microsoft.Orleans.Client/) | Includes everything you need for an Orleans client (frontend), in a process or an Azure Web Role.

Other packages are either included by Microsoft.Orleans.Client or Microsoft.Orleans.Server or are for rare use cases.

Package   | Purpose
------------- | -------------
[Microsoft.Orleans.Core](http://www.nuget.org/packages/Microsoft.Orleans.Core/) | Contains Orleans.dll, which defines most of Orleans public types and Orleans Client. Included in Microsoft.Orleans.Client and Microsoft.Orleans.Server. Reference it for building libraries that use Orleans types but don't need any of the dependnecies of code generation.
[Microsoft.Orleans.OrleansRuntime](http://www.nuget.org/packages/Microsoft.Orleans.OrleansRuntime/) | Contains the silo runtime. Included in Microsoft.Orleans.Server.
[Microsoft.Orleans.OrleansAzureUtils](http://www.nuget.org/packages/Microsoft.Orleans.OrleansAzureUtils/) | Contains Orleans dependecies on Azure SDK libraries. Included in Microsoft.Orleans.Client and Microsoft.Orleans.Server. May become optional when we switch from static dependency on OrleansAzureUtils.dll to dynamic on-demand loading of it.
[Microsoft.Orleans.OrleansProviders](http://www.nuget.org/packages/Microsoft.Orleans.OrleansProviders/) | Includes a set of built-in persistence and stream providers. Included in Microsoft.Orleans.Client and Microsoft.Orleans.Server.
[Microsoft.Orleans.OrleansHost](http://www.nuget.org/packages/Microsoft.Orleans.OrleansHost/) | Includes default silo host - OrleansHost.exe. Included in Microsoft.Orleans.Server. Can be used for on-premises deployments or as an out-of-process silo host in an Azure Worker Role.
[Microsoft.Orleans.CounterControl](http://www.nuget.org/packages/Microsoft.Orleans.CounterControl/) | Includes CounterControl.exe, which registers Windows performance counter categories for Orleans statistics and for deployed grain classes. Requires elevation. Can be executed in Azure as part of a role startup task.Included in Microsoft.Orleans.Server.
[Microsoft.Orleans.OrleansManager](http://www.nuget.org/packages/Microsoft.Orleans.OrleansManager/) | Includes Orleans management tool - OrleansManager.exe.

Packages Microsoft.Orleans.Development and Microsoft.Orleans.ClientGenerator that existed prior to v1.0.5 got deprecated with their content and functionality now included in Microsoft.Orleans.Templates.Interfaces and Microsoft.Orleans.Templates.Grains.
