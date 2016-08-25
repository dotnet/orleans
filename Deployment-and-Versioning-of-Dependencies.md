---
layout: page
title: Deployment and Versioning of Dependencies
---
{% include JB/setup %}


## Orleans Dependencies ##

Orleans currently references the following external dependencies.

**Windows Azure SDK 2.4** (several NuGet packages), although Orleans can run outside of Azure and with no run time dependency on it.

**Newtonsoft.Json 5.0.8**

The exact versions of the dependencies will change over time. We will use the current versions to illustrate the deployment and versioning options. The general rule we are trying to follow with regards to Azure SDK is to target its version current-1, to make the transition to new versions of it easier. As of this writing the current version is 2.5, and hence we target 2.4.

## Versioning of Orleans dependencies ##

A typical use case is when you need to run Orleans, silo or client, with a different version of a dependent library, for example with Azure SDK 2.5. In this scenario, Orleans works just like any other .NET library being subject to assembly binding redirect rules. All you need to do is to add a set of binding redirect settings to the app.config file of the process.

Here's an example of an app.config that redirects Microsoft.WindowsAzure.Storage and Microsoft.WindowsAzure.Storage to the versions included in Azure SDK 2.5 and Azure Newtonsoft.Json to version 6.0.0.0.

``` xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <runtime>
    <assemblyBinding xmlns="urn:schemas-microsoft-com:asm.v1">
      <dependentAssembly>
        <assemblyIdentity name="Microsoft.WindowsAzure.Storage" publicKeyToken="31bf3856ad364e35" culture="neutral" />
        <bindingRedirect oldVersion="0.0.0.0-4.3.0.0" newVersion="4.3.0.0" />
      </dependentAssembly>
      <dependentAssembly>
        <assemblyIdentity name="Microsoft.WindowsAzure.ServiceRuntime" publicKeyToken="31bf3856ad364e35" culture="neutral" />
        <bindingRedirect oldVersion="0.0.0.0-2.5.0.0" newVersion="2.5.0.0" />
      </dependentAssembly>
      <dependentAssembly>
        <assemblyIdentity name="Newtonsoft.Json" publicKeyToken="30ad4fe6b2a6aeed" culture="neutral" />
        <bindingRedirect oldVersion="0.0.0.0-6.0.0.0" newVersion="6.0.0.0" />
      </dependentAssembly>
    </assemblyBinding>
  </runtime>
</configuration>
```

## Grain assemblies deployed to subfolder ##

The recommended way to deploy application code, grain assemblies and their dependencies, is to put them into a subfolder of the Applications folder. This approach provides a clear separation of application code from the Orleans runtime code and allows for additional flexibility with dependencies. You can (but don't have to) have some of the same assemblies that are already present in the main Orleans folder but of different versions.

For example, if your code depends on a library that is incompatible with, say, Azure Storage 4.3, you can simply put a compatible version of Microsoft.WindowsAzure.Storage.dll (and its dependencies) into your application folder. In that case, the two versions will be loaded by the CLR side by side.

## Grain assemblies deployed to main folder ##

If for some reason deploying grain assemblies into a subfolder is not an option, and you have to deploy grain assemblies into the main folder, you are obviously limited to just one set of dependency assemblies. So you need to choose a set of versions that are compatible with both the Orleans runtime and your grain code. If necessary, you can specify assembly binding redirects as in the example above. 
