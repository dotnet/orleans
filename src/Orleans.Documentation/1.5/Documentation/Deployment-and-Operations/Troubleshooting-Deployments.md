---
layout: page
title: Troubleshooting Deployments
---

[!include[](../../warning-banner.md)]

# Troubleshooting Deployments

This page gives some general guidelines for troubleshooting any issues that occur while deploying to Azure Cloud Services.
These are very common issues to watch out for. Be sure to check the logs for more information.

## Getting a SiloUnavailableException

First check to make sure that you are actually starting the silos before attempting to initialize the client. Sometimes the
silos take a long time to start so it can be beneficial to try to initialize the client multiple times. If it still throws an
exception, then there might be another issue with the silos.

Check the silo configuration and make sure that the silos are starting up properly.

## Common Connection String Issues
-	Using the local connection string when deploying to Azure – the website will fail to connect
-	Using different connection strings for the silos and the front end (web and worker roles) – the website will fail to
initialize the client because it cannot connect to the silos

The connection string configuration can be checked in the Azure Portal. The logs may not display properly if the connection
strings are not set up correctly.

## Modifying the Configuration Files Improperly

Make sure that the proper endpoints are configured in the ServiceDefinition.csdef file or else the deployment will not work.
It will give errors saying that it cannot get the endpoint information.

## Missing Logs
Make sure that the connection strings are set up properly.

It is likely that the Web.config file in the web role or the app.config file in the worker role were modified improperly.
Incorrect versions in these files can cause issues with the deployment. Be careful when dealing with updates.

## Version Issues
Make sure that the same version of Orleans is used in every project in the solution. Not doing this can lead to the worker
role recycling. Check the logs for more information. Visual Studio provides some silo startup error messages in the deployment history.

## Role Keeps Recycling
- Check that all the appropriate Orleans assemblies are in the solution and have Copy Local set to True.
- Check the logs to see if there is an unhandled exception while initializing.
- Make sure that the connection strings are correct.
- Check the Azure Cloud Services troubleshooting pages for more information.

## How to Check Logs
- Use the cloud explorer in Visual Studio to navigate to the appropriate storage table or blob in the storage account. The WADLogsTable is a good starting point for looking at the logs.
- You might only be logging errors. If you want informational logs as well, you will need to modify the configuration to set the logging severity level.

Programmatic configuration:
- When creating a `ClusterConfiguration` object, set `config.Defaults.DefaultTraceLevel = Severity.Info`.
- When creating a `ClientConfiguration` object, set `config.DefaultTraceLevel = Severity.Info`.

Declarative configuration:
- Add `<Tracing DefaultTraceLevel="Info" />` to the `OrleansConfiguration.xml` and/or the `ClientConfiguration.xml` files.

In the `diagnostics.wadcfgx` file for the web and worker roles, make sure to set the `scheduledTransferLogLevelFilter` attribute in the `Logs` element to `Information`, as this is an additional layer of trace filtering that defines which traces are sent to the `WADLogsTable` in Azure Storage.

You can find more information about this in the [Configuration Guide] (Configuration-Guide/index.md).

## Compatibility with ASP.NET

The razor view engine included in ASP.NET uses the same code generation assemblies as Orleans (`Microsoft.CodeAnalysis` and `Microsoft.CodeAnalysis.CSharp`). This can present a version compatibility problem at runtime.

To resolve this, try upgrading `Microsoft.CodeDom.Providers.DotNetCompilerPlatform` (this is the NuGet package ASP.NET uses to include the above assemblies) to the latest version, and setting binding redirects like this:

```xml
<dependentAssembly>
  <assemblyIdentity name="Microsoft.CodeAnalysis.CSharp" publicKeyToken="31bf3856ad364e35" culture="neutral" />
  <bindingRedirect oldVersion="0.0.0.0-2.0.0.0" newVersion="1.3.1.0" />
</dependentAssembly>
<dependentAssembly>
  <assemblyIdentity name="Microsoft.CodeAnalysis" publicKeyToken="31bf3856ad364e35" culture="neutral" />
  <bindingRedirect oldVersion="0.0.0.0-2.0.0.0" newVersion="1.3.1.0" />
</dependentAssembly>
```
