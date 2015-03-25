---
layout: page
title: Orleans NuGet Packages
---
{% include JB/setup %}

# Orleans NuGet packages as of v1.0.5

In v1.0.5 we made important chnages to the set of NuGet packages. With these changes, grain interface and grain class projects can now be created and built without Orleans SDK installed. We also retired two packages with this change.

There are four key NuGet packages you will use in most scenarios.

Package   | Purpose
------------- | -------------
[Microsoft.Orleans.Templates.Interfaces](http://www.nuget.org/packages/Microsoft.Orleans.Templates.Interfaces/) | Build support for grain interfaces projects. Add it to your project to enable code generation for grain interfaces.
[Microsoft.Orleans.Templates.Grains](http://www.nuget.org/packages/Microsoft.Orleans.Templates.Grains/) | Build support for grain implementation projects. Add it to your project to enable code generation for grain classes.
[Microsoft.Orleans.Server](http://www.nuget.org/packages/Microsoft.Orleans.Server/) | Includes everything you need to host a silo, in a process or an Azure Worker Role.
[Microsoft.Orleans.Client](http://www.nuget.org/packages/Microsoft.Orleans.Client/) | Includes everything you need for an Orleans client (frontend), in a process or an Azure Web Role.
