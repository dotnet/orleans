---
title: Troubleshoot Azure Cloud Service deployments
description: Learn how to troubleshoot an Orleans app deployed to Azure Cloud Services.
ms.date: 01/21/2026
ms.topic: troubleshooting
ms.custom: devops
---

# Troubleshoot Azure Cloud Service deployments

> [!CAUTION]
> **Azure Cloud Services (classic) is retired.** This article is provided for legacy reference only. Azure Cloud Services (classic) was retired on August 31, 2024. For new Orleans deployments, use one of the following modern hosting options:
>
> - [Azure Kubernetes Service (AKS)](kubernetes.md)
> - [Azure Container Apps](deploy-to-azure-container-apps.md)
> - [Azure App Service](deploy-to-azure-app-service.md)
> - [Service Fabric](service-fabric.md)
>
> For general deployment troubleshooting, see [Troubleshoot deployments](troubleshooting-deployments.md).

This page provides general guidelines for troubleshooting issues occurring when deploying to Azure Cloud Services. These are common issues to watch out for. Check the logs for more detailed information.

## SiloUnavailableException

If your client throws a <xref:Orleans.Runtime.SiloUnavailableException>, first ensure silos start before attempting to initialize the client. Sometimes silos take a long time to start, so trying to initialize the client multiple times can be beneficial. If it still throws an exception, another issue might exist with the silos.

Check the silo configuration and ensure the silos start properly.

## Common connection string issues

- **Using the local connection string when deploying to Azure**: The website fails to connect.
- **Using different connection strings for silos and the front end (web and worker roles)**: The website fails to initialize the client because it cannot connect to the silos.

Check the connection string configuration in the Azure portal. Logs might not display properly if connection strings aren't set up correctly.

## Improperly modified configuration files

Ensure proper endpoints are configured in the _ServiceDefinition.csdef_ file; otherwise, the deployment won't work. Errors stating that endpoint information cannot be obtained will occur.

## Missing logs

Ensure the connection strings are set up properly.

It's likely the _Web.config_ file in the web role or the _app.config_ file in the worker role was modified improperly. Incorrect versions in these files can cause deployment issues. Be careful when handling updates.

## Version issues

Ensure the same version of Orleans is used in every project in the solution. Using different versions can lead to worker role recycling. Check the logs for more information. Visual Studio provides some silo startup error messages in the deployment history.

## Role keeps recycling

- Verify all appropriate Orleans assemblies are in the solution and have **Copy Local** set to **True**.
- Check the logs for unhandled exceptions during initialization.
- Ensure the connection strings are correct.
- Refer to the Azure Cloud Services troubleshooting pages for more information.

## How to check logs

- Use Cloud Explorer in Visual Studio to navigate to the appropriate storage table or blob in the storage account. The `WADLogsTable` is a good starting point for examining logs.
- Only errors might be logged. If informational logs are also desired, modify the configuration to set the logging severity level.

Programmatic configuration:

- When creating a <xref:Orleans.Runtime.Configuration.ClusterConfiguration> object, set `config.Defaults.DefaultTraceLevel = Severity.Info`.
- When creating a <xref:Orleans.Runtime.Configuration.ClientConfiguration> object, set `config.DefaultTraceLevel = Severity.Info`.

Declarative configuration:

- Add `<Tracing DefaultTraceLevel="Info" />` to the _OrleansConfiguration.xml_ and/or _ClientConfiguration.xml_ files.

In the _diagnostics.wadcfgx_ file for the web and worker roles, ensure the `scheduledTransferLogLevelFilter` attribute in the `Logs` element is set to `Information`. This setting acts as an additional layer of trace filtering defining which traces are sent to the `WADLogsTable` in Azure Storage.

Find more information about this in the configuration guide.

## Compatibility with ASP.NET

The Razor view engine included in ASP.NET uses the same code generation assemblies as Orleans (`Microsoft.CodeAnalysis` and `Microsoft.CodeAnalysis.CSharp`). This can present a version compatibility problem at runtime.

To resolve this, try upgrading `Microsoft.CodeDom.Providers.DotNetCompilerPlatform` (the NuGet package ASP.NET uses to include these assemblies) to the latest version and setting binding redirects like this:

```xml
<dependentAssembly>
    <assemblyIdentity name="Microsoft.CodeAnalysis.CSharp"
        publicKeyToken="31bf3856ad364e35" culture="neutral" />
    <bindingRedirect oldVersion="0.0.0.0-2.0.0.0" newVersion="1.3.1.0" />
</dependentAssembly>
<dependentAssembly>
    <assemblyIdentity name="Microsoft.CodeAnalysis"
        publicKeyToken="31bf3856ad364e35" culture="neutral" />
    <bindingRedirect oldVersion="0.0.0.0-2.0.0.0" newVersion="1.3.1.0" />
</dependentAssembly>
```
