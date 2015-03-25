---
layout: page
title: Orleans NuGet Packages
---
{% include JB/setup %}


### Orleans NuGet packages as of [v1.0.5](https://github.com/dotnet/orleans/releases/tag/v1.0.5)


In [v1.0.5](https://github.com/dotnet/orleans/releases/tag/v1.0.5) we made important changes to the set of NuGet packages. With these changes, grain interfaces and grain implementation projects can now be created and built without [Orleans SDK installed](http://dotnet.github.io/orleans/Installation). We also retired two old packages with this change.

There are 4 key NuGet packages you will need to use in most scenarios:

Package   | Purpose
------------- | -------------
[Microsoft.Orleans.Templates.Interfaces](http://www.nuget.org/packages/Microsoft.Orleans.Templates.Interfaces/) | Build support for grain interfaces projects. Add it to your grain interfaces project to enable code generation for grain interfaces.
[<br>Microsoft.Orleans.Templates.Grains](http://www.nuget.org/packages/Microsoft.Orleans.Templates.Grains/) | <br>Build support for grain implementation projects. Add it to your grain implementation project to enable code generation for grain classes.
[<br>Microsoft.Orleans.Server](http://www.nuget.org/packages/Microsoft.Orleans.Server/) | <br>Includes everything you need to host a silo, in a process or in Azure Worker Role.
[<br>Microsoft.Orleans.Client](http://www.nuget.org/packages/Microsoft.Orleans.Client/) | <br>Includes everything you need for an Orleans client (frontend), in a process or in Azure Web Role.

<br>

The below aditional 7 packages are included by either [Microsoft.Orleans.Client](http://www.nuget.org/packages/Microsoft.Orleans.Client/) or [Microsoft.Orleans.Server](http://www.nuget.org/packages/Microsoft.Orleans.Server/), and can also be used separately for rare use cases.

Package   | Purpose
------------- | -------------
[Microsoft.Orleans.Core](http://www.nuget.org/packages/Microsoft.Orleans.Core/) | Contains Orleans.dll, which defines most of Orleans public types and Orleans Client. Reference it for building libraries that use Orleans types but don't need any of the dependencies of code generation. Included in Microsoft.Orleans.Client and Microsoft.Orleans.Server.
[<br>Microsoft.Orleans.OrleansRuntime](http://www.nuget.org/packages/Microsoft.Orleans.OrleansRuntime/) | <br>Contains the silo runtime. Included in Microsoft.Orleans.Server.
[<br>Microsoft.Orleans.OrleansAzureUtils](http://www.nuget.org/packages/Microsoft.Orleans.OrleansAzureUtils/) | <br> Contains Orleans dependecies on Azure SDK libraries, such as Azure Runtime. May become optional when we switch from static dependency on OrleansAzureUtils.dll to dynamic on-demand loading of it. Included in Microsoft.Orleans.Client and Microsoft.Orleans.Server.
[<br>Microsoft.Orleans.OrleansProviders](http://www.nuget.org/packages/Microsoft.Orleans.OrleansProviders/) | <br>Includes a set of built-in persistence and stream providers. Included in Microsoft.Orleans.Client and Microsoft.Orleans.Server.
[<br>Microsoft.Orleans.OrleansHost](http://www.nuget.org/packages/Microsoft.Orleans.OrleansHost/) | <br>Includes a default silo host - OrleansHost.exe. Can be used for on-premises deployments or as an out-of-process silo host in Azure Worker Role. Included in Microsoft.Orleans.Server.
[<br>Microsoft.Orleans.CounterControl](http://www.nuget.org/packages/Microsoft.Orleans.CounterControl/) | <br>Includes CounterControl.exe, which registers Windows performance counter categories for Orleans statistics and for deployed grain classes. Requires elevation. Can be executed in Azure as part of a role startup task. Included in Microsoft.Orleans.Server.
[<br>Microsoft.Orleans.OrleansManager](http://www.nuget.org/packages/Microsoft.Orleans.OrleansManager/) | <br>Includes Orleans management tool - OrleansManager.exe. Not included in any other package.

<br>

Packages [Microsoft.Orleans.Development](http://www.nuget.org/packages/Microsoft.Orleans.Development/) and [Microsoft.Orleans.ClientGenerator](http://www.nuget.org/packages/Microsoft.Orleans.ClientGenerator/) that existed prior to v1.0.5 got deprecated with their content and functionality now included in [Microsoft.Orleans.Templates.Interfaces](http://www.nuget.org/packages/Microsoft.Orleans.Templates.Interfaces/) and [Microsoft.Orleans.Templates.Grains](http://www.nuget.org/packages/Microsoft.Orleans.Templates.Grains/).
