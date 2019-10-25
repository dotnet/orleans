---
layout: page
title: Orleans NuGet Packages
---

# Orleans NuGet packages

## Key Packages

There are 5 key NuGet packages you will need to use in most scenarios:

### [Microsoft Orleans Core Abstractions](https://www.nuget.org/packages/Microsoft.Orleans.Core.Abstractions/)

``` powershell
PM> Install-Package Microsoft.Orleans.Core.Abstractions
```

Contains Orleans.Core.Abstractions.dll, which defines Orleans public types that are needed for developing application code (grain interfaces and classes).
This package is needs to be directly or indirectly referenced by any Orleans project.
Add it to your projects that define grain interfaces and classes.

### Microsoft Orleans Build-time Code Generation

* [Microsoft.Orleans.OrleansCodeGenerator.Build](https://www.nuget.org/packages/Microsoft.Orleans.OrleansCodeGenerator.Build/).

    ``` powershell
    PM> Install-Package Microsoft.Orleans.OrleansCodeGenerator.Build
    ```

    Appeared in Orleans 1.2.0. Build time support for grain interfaces and implementation projects.
    Add it to your grain interfaces and implementation projects to enable code generation of grain references and serializers.

* [Microsoft.Orleans.CodeGenerator.MSBuild](https://www.nuget.org/packages/Microsoft.Orleans.CodeGenerator.MSBuild/).

    ``` powershell
    PM> Install-Package Microsoft.Orleans.CodeGenerator.MSBuild
    ```

    Appeared as part of [Orleans 2.1.0](https://blogs.msdn.microsoft.com/orleans/2018/10/01/announcing-orleans-2-1/). An alternative to the `Microsoft.Orleans.OrleansCodeGenerator.Build` package. Leverages Roslyn for code analysis to avoid loading application binaries and improves support for incremental builds, which should result in shorter build times.

### [Microsoft Orleans Server Libraries](https://www.nuget.org/packages/Microsoft.Orleans.Server/)

``` powershell
PM> Install-Package Microsoft.Orleans.Server
```

A meta-package for easily building and starting a silo. Includes the following packages:

* Microsoft.Orleans.Core.Abstractions
* Microsoft.Orleans.Core
* Microsoft.Orleans.OrleansRuntime
* Microsoft.Orleans.OrleansProviders

### [Microsoft Orleans Client Libraries](https://www.nuget.org/packages/Microsoft.Orleans.Client/)

``` powershell
PM> Install-Package Microsoft.Orleans.Client
```

A meta-package for easily building and starting an Orleans client (frontend). Includes the following packages:

* Microsoft.Orleans.Core.Abstractions
* Microsoft.Orleans.Core
* Microsoft.Orleans.OrleansProviders

### [Microsoft Orleans Core Library](https://www.nuget.org/packages/Microsoft.Orleans.Core/)

``` powershell
PM> Install-Package Microsoft.Orleans.Core
```

Contains implementation for most Orleans public types used by application code and Orleans clients (frontends).
Reference it for building libraries and client applications that use Orleans types but don't deal with hosting or silos.
Included in Microsoft.Orleans.Client and Microsoft.Orleans.Server meta-packages, and is referenced, directly or indirectly, by most other packages. 

## Hosting

### [Microsoft Orleans Runtime](https://www.nuget.org/packages/Microsoft.Orleans.OrleansRuntime/)

``` powershell
PM> Install-Package Microsoft.Orleans.OrleansRuntime 
```

Library for configuring and starting a silo. Reference it in your silo host project. Included in Microsoft.Orleans.Server meta-package.

### [Microsoft Orleans Runtime Abstractions](https://www.nuget.org/packages/Microsoft.Orleans.Runtime.Abstractions/)

``` powershell
PM> Install-Package Microsoft.Orleans.Runtime.Abstractions 
```

Contains interfaces and abstractions for types implemented in Microsoft.Orleans.OrleansRuntime.

### [Microsoft Orleans Hosting on Azure Cloud Services](https://www.nuget.org/packages/Microsoft.Orleans.Hosting.AzureCloudServices/)

``` powershell
PM> Install-Package Microsoft.Orleans.Hosting.AzureCloudServices
```

Contains helper classes for hosting silos and Orleans clients as Azure Cloud Services (Worker Roles and Web Roles).

### [Microsoft Orleans Service Fabric Hosting Support](https://www.nuget.org/packages/Microsoft.Orleans.Hosting.ServiceFabric/)

``` powershell
PM> Install-Package Microsoft.Orleans.Hosting.ServiceFabric 
```

Contains helper classes for hosting silos as a stateless Service Fabric service.

## Clustering Providers

The below packages include plugins for persisting cluster membership data in various storage technologies.

### [Microsoft Orleans clustering provider for Azure Table Storages](https://www.nuget.org/packages/Microsoft.Orleans.Clustering.AzureStorage/)

``` powershell
PM> Install-Package Microsoft.Orleans.Clustering.AzureStorage
```

Includes the plugin for using Azure Tables for storing cluster membership data.

### [Microsoft Orleans clustering provider for ADO.NET Providers](https://www.nuget.org/packages/Microsoft.Orleans.Clustering.AdoNet/)

``` powershell
PM> Install-Package Microsoft.Orleans.Clustering.AdoNet
```

Includes the plugin for using ADO.NET for storing cluster membership data in one of the supported databases.

### [Microsoft Orleans Consul Utilities](https://www.nuget.org/packages/Microsoft.Orleans.OrleansConsulUtils/)

``` powershell
PM> Install-Package Microsoft.Orleans.OrleansConsulUtils
```

Includes the plugin for using Consul for storing cluster membership data.

### [Microsoft Orleans ZooKeeper Utilities](https://www.nuget.org/packages/Microsoft.Orleans.OrleansZooKeeperUtils/)

``` powershell
PM> Install-Package Microsoft.Orleans.OrleansZooKeeperUtils
```

Includes the plugin for using ZooKeeper for storing cluster membership data.

### [Microsoft Orleans clustering provider for AWS DynamoDB](https://www.nuget.org/packages/Microsoft.Orleans.Clustering.DynamoDB/)

``` powershell
PM> Install-Package Microsoft.Orleans.Clustering.DynamoDB
```

Includes the plugin for using AWS DynamoDB for storing cluster membership data.

## Reminder Providers

The below packages include plugins for persisting reminders in various storage technologies.

### [Microsoft Orleans Reminders Azure Table Storage](https://www.nuget.org/packages/Microsoft.Orleans.Reminders.AzureStorage/)

``` powershell
PM> Install-Package Microsoft.Orleans.Reminders.AzureStorage
```

Includes the plugin for using Azure Tables for storing reminders.

### [Microsoft Orleans Reminders ADO.NET Providers](https://www.nuget.org/packages/Microsoft.Orleans.reminders.AdoNet/)

``` powershell
PM> Install-Package Microsoft.Orleans.Reminders.AdoNet
```

Includes the plugin for using ADO.NET for storing reminders in one of the supported databases.

### [Microsoft Orleans reminders provider for AWS DynamoDB](https://www.nuget.org/packages/Microsoft.Orleans.Reminders.DynamoDB/)

``` powershell
PM> Install-Package Microsoft.Orleans.Reminders.DynamoDB
```

Includes the plugin for using AWS DynamoDB for storing reminders.

## Grain Storage Providers

The below packages include plugins for persisting grain state in various storage technologies.

### [Microsoft Orleans Persistence Azure Storage](https://www.nuget.org/packages/Microsoft.Orleans.Persistence.AzureStorage/)

``` powershell
PM> Install-Package Microsoft.Orleans.Persistence.AzureStorage
```

Includes the plugins for using Azure Tables or Azure Blobs for storing grain state.

### [Microsoft Orleans Persistence ADO.NET Providers](https://www.nuget.org/packages/Microsoft.Orleans.Persistence.AdoNet/)

``` powershell
PM> Install-Package Microsoft.Orleans.Persistence.AdoNet
```

Includes the plugin for using ADO.NET for storing grain state in one of the supported databases.

### [Microsoft Orleans Persistence DynamoDB](https://www.nuget.org/packages/Microsoft.Orleans.Persistence.DynamoDB/)

``` powershell
PM> Install-Package Microsoft.Orleans.Persistence.DynamoDB
```

Includes the plugin for using AWS DynamoDB for storing grain state.

## Stream Providers

The below packages include plugins for delivering streaming events.

### [Microsoft Orleans ServiceBus Utilities](https://www.nuget.org/packages/Microsoft.Orleans.OrleansServiceBus/)

``` powershell
PM> Install-Package Microsoft.Orleans.OrleansServiceBus
```

Includes the stream provider for Azure Event Hubs.

### [Microsoft Orleans Streaming Azure Storage](https://www.nuget.org/packages/Microsoft.Orleans.Streaming.AzureStorage/)

``` powershell
PM> Install-Package Microsoft.Orleans.Streaming.AzureStorage
```

Includes the stream provider for Azure Queues.

### [Microsoft Orleans Streaming AWS SQS](https://www.nuget.org/packages/Microsoft.Orleans.Streaming.SQS/)

``` powershell
PM> Install-Package Microsoft.Orleans.Streaming.SQS
```

Includes the stream provider for AWS SQS service.

### [Microsoft Orleans Google Cloud Platform Utilities](https://www.nuget.org/packages/Microsoft.Orleans.OrleansGCPUtils/)

``` powershell
PM> Install-Package Microsoft.Orleans.OrleansGCPUtils
```

Includes the stream provider for GCP PubSub service.

## Additional Packages

### [Microsoft Orleans Code Generation](https://www.nuget.org/packages/Microsoft.Orleans.OrleansCodeGenerator/)

``` powershell
PM> Install-Package Microsoft.Orleans.OrleansCodeGenerator
```

Includes the run time code generator.

### [Microsoft Orleans Event-Sourcing](https://www.nuget.org/packages/Microsoft.Orleans.EventSourcing/)

``` powershell
PM> Install-Package Microsoft.Orleans.EventSourcing 
```

Contains a set of base types for creating grain classes with event-sourced state.

## Development and Testing

### [Microsoft Orleans Providers](https://www.nuget.org/packages/Microsoft.Orleans.OrleansProviders/)

``` powershell
PM> Install-Package Microsoft.Orleans.OrleansProviders
```

Contains a set of persistence and stream providers that keep data in memory.
Intended for testing.
In general, not recommended for production use, unless data loss is care of a silo failure is acceptable.

### [Microsoft Orleans Testing Host Library](https://www.nuget.org/packages/Microsoft.Orleans.TestingHost/)

``` powershell
PM> Install-Package Microsoft.Orleans.TestingHost
```

Includes the library for hosting silos and clients in a testing project.

## Legacy Packages

The below packages are for backward compatibility and easier migration from Orleans 1.x to 2.0

### [Microsoft Orleans Core Legacy Library](https://www.nuget.org/packages/Microsoft.Orleans.Core.Legacy/)

``` powershell
PM> Install-Package Microsoft.Orleans.Core.Legacy
```

Contains 1.x style client configuration objects and logging APIs. Makes migration easier by not requiring to change client code to the new client builder API and logging.

### [Microsoft Orleans Runtime Legacy Library](https://www.nuget.org/packages/Microsoft.Orleans.Runtime.Legacy/)

``` powershell
PM> Install-Package Microsoft.Orleans.Runtime.Legacy
```

Contains 1.x style silo configuration objects and hosting APIs. Makes migration easier by not requiring to change silo configuration and hosting code to the new silo host builder API.

### [Microsoft Orleans Azure Utilities](https://www.nuget.org/packages/Microsoft.Orleans.OrleansAzureUtils/)

``` powershell
PM> Install-Package Microsoft.Orleans.OrleansAzureUtils
```

A meta-package that includes all packages with Azure providers to simplify upgrading of 1.x projects.

### [Microsoft Orleans Sql Utilities](https://www.nuget.org/packages/Microsoft.Orleans.OrleansSqlUtils/)

``` powershell
PM> Install-Package Microsoft.Orleans.OrleansSqlUtils 
```

A meta-package that includes all packages with ADO.NET providers to simplify upgrading of 1.x projects.

### [Microsoft Orleans AWS Utilities](https://www.nuget.org/packages/Microsoft.Orleans.OrleansAWSUtils/)

``` powershell
PM> Install-Package Microsoft.Orleans.OrleansAWSUtils
```

A meta-package that includes all packages with AWS providers to simplify upgrading of 1.x projects.

### [Microsoft Orleans Service Fabric Support](https://www.nuget.org/packages/Microsoft.Orleans.ServiceFabric/)

``` powershell
PM> Install-Package Microsoft.Orleans.ServiceFabric
```

A meta-package that includes all packages with Service Fabric providers to simplify upgrading of 1.x projects.

### [Microsoft Orleans Management Tool](https://www.nuget.org/packages/Microsoft.Orleans.OrleansManager/)

``` powershell
PM> Install-Package Microsoft.Orleans.OrleansManager
```

Includes Orleans management tool - OrleansManager.exe.

## Serializers

### [Microsoft Orleans Bond Serializer](https://www.nuget.org/packages/Microsoft.Orleans.Serialization.Bond/)

``` powershell
PM> Install-Package Microsoft.Orleans.Serialization.Bond
```

Includes support for [Bond serializer](https://github.com/microsoft/bond).

### [Microsoft Orleans Google Utilities](https://www.nuget.org/packages/Microsoft.Orleans.OrleansGoogleUtils/)

``` powershell
PM> Install-Package Microsoft.Orleans.OrleansGoogleUtils
```

Includes Google Protocol Buffers serializer.

### [Microsoft Orleans protobuf-net Serializer](https://www.nuget.org/packages/Microsoft.Orleans.ProtobufNet/)

``` powershell
PM> Install-Package Microsoft.Orleans.ProtobufNet
```

Includes protobuf-net version of Protocol Buffers serializer.

## Telemetry

### [Microsoft Orleans Telemetry Consumer - Performance Counters](https://www.nuget.org/packages/Microsoft.Orleans.OrleansTelemetryConsumers.Counters/)

``` powershell
PM> Install-Package Microsoft.Orleans.OrleansTelemetryConsumers.Counters
```

Windows Performance Counters implementation of Orleans Telemetry API.

### [Microsoft Orleans Telemetry Consumer - Azure Application Insights](https://www.nuget.org/packages/Microsoft.Orleans.OrleansTelemetryConsumers.AI/)

``` powershell
PM> Install-Package Microsoft.Orleans.OrleansTelemetryConsumers.AI
```

Includes the telemetry consumer for Azure Application Insights.

### [Microsoft Orleans Telemetry Consumer - NewRelic](https://www.nuget.org/packages/Microsoft.Orleans.OrleansTelemetryConsumers.NewRelic/)

``` powershell
PM> Install-Package Microsoft.Orleans.OrleansTelemetryConsumers.NewRelic
```

Includes the telemetry consumer for NewRelic.

## Tools

### [Microsoft Orleans Performance Counter Tool](https://www.nuget.org/packages/Microsoft.Orleans.CounterControl/)

``` powershell
PM> Install-Package Microsoft.Orleans.CounterControl
```

Includes OrleansCounterControl.exe, which registers Windows performance counter categories for Orleans statistics and for deployed grain classes. Requires elevation. Can be executed in Azure as part of a role startup task.

## Transactions

### [Microsoft Orleans Transactions support](https://www.nuget.org/packages/Microsoft.Orleans.Transactions/)

``` powershell
PM> Install-Package Microsoft.Orleans.Transactions
```

Includes support for cross-grain transactions (beta).

### [Microsoft Orleans Transactions on Azure](https://www.nuget.org/packages/Microsoft.Orleans.Transactions.AzureStorage/)

``` powershell
PM> Install-Package Microsoft.Orleans.Transactions.AzureStorage
```

Includes a plugin for persisting transaction log in Azure Table (beta).
