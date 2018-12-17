---
layout: page
title: Server Configuration
---

> [!NOTE]
> If you want to start a local silo and a local client for development purposes, look at the [Local Development Configuration page](local_development_configuration.md)

# Server Configuration

A silo is configured programmatically via `SiloHostBuilder` and a number of supplemental option classes.
Option classes in Orleans follow the [ASP.NET Options](https://docs.microsoft.com/en-us/aspnet/core/fundamentals/configuration/options) pattern, and can be loaded via files, environment variables, etc.

There are several key aspects of silo configuration:

* Orleans clustering information
* Clustering provider
* Endpoints to use for silo-to-silo and client-to-silo communications
* Application parts

This is an example of a silo configuration that defines cluster information, uses Azure clustering, and configures the application parts:

``` csharp
var silo = new SiloHostBuilder()
    // Clustering information
    .Configure<ClusterOptions>(options =>
    {
        options.ClusterId = "my-first-cluster";
        options.ServiceId = "MyAwesomeOrleansService";
    })
    // Clustering provider
    .UseAzureStorageClustering(options => options.ConnectionString = connectionString)
    // Endpoints
    .ConfigureEndpoints(siloPort: 11111, gatewayPort: 30000)
    // Application parts: just reference one of the grain implementations that we use
    .ConfigureApplicationParts(parts => parts.AddApplicationPart(typeof(ValueGrain).Assembly).WithReferences())
    // Now create the silo!
    .Build();
```

Let's breakdown the steps used in this sample:

## Orleans clustering information

``` csharp
    [...]
    // Clustering information
    .Configure<ClusterOptions>(options =>
    {
        options.ClusterId = "orleans-docker";
        options.ServiceId = "AspNetSampleApp";
    })
    [...]
```

Here we do two things:

* Set the `ClusterId` to `"my-first-cluster"`: this is a unique ID for the Orleans cluster. All clients and silos that use this ID will be able to talk directly to each other. You can choose to use a different `ClusterId` for different deployments, though.
* Set the `ServiceId` to `"AspNetSampleApp"`: this is a unique ID for your application that will be used by some providers, such as persistence providers. **This ID should remain stable and not change across deployments**.

## Clustering provider

``` csharp
    [...]
    // Clustering provider
    .UseAzureStorageClustering(options => options.ConnectionString = connectionString)
    [...]
```

 Usually, a service built on Orleans is deployed on a cluster of nodes, either on dedicated hardware or in Azure.
 For development and basic testing, Orleans can be deployed in a single node configuration.
 When deployed to a cluster of nodes, Orleans internally implements a set of protocols to discover and maintain membership of Orleans silos in the cluster, including detection of node failures and automatic reconfiguration.

 For reliable management of cluster membership, Orleans uses Azure Table, SQL Server, or Apache ZooKeeper for synchronization of nodes.

 In this sample, we are using Azure Table as the membership provider.

## Endpoints

``` csharp
var silo = new SiloHostBuilder()
    [...]
    // Endpoints
    .ConfigureEndpoints(siloPort: 11111, gatewayPort: 30000)
    [...]
```

An Orleans silo has two typical types of endpoint configuration:

* Silo-to-silo endpoints, used for communication between silos in the same cluster
* Client-to-silo endpoints (or gateway), used for communication between clients and silos in the same cluster

In the sample, we are using the helper method `.ConfigureEndpoints(siloPort: 11111, gatewayPort: 30000)` which sets the port used for silo-to-silo communication to `11111` and and the port for the gateway to `30000`.
This method will detect which interface to listen to.

This method should be sufficient in most cases, but you can customize it further if you need to.
Here is an example of how to use an external IP address with some port-forwarding:

``` csharp
[...]
.Configure<EndpointOptions>(options =>
{
    // Port to use for Silo-to-Silo
    options.SiloPort = 11111;
    // Port to use for the gateway
    options.GatewayPort = 30000;
    // IP Address to advertise in the cluster
    options.AdvertisedIPAddress = IPAddress.Parse("172.16.0.42");
    // The socket used for silo-to-silo will bind to this endpoint
    options.GatewayListeningEndpoint = new IPEndPoint(IPAddress.Any, 40000);
    // The socket used by the gateway will bind to this endpoint
    options.SiloListeningEndpoint = new IPEndPoint(IPAddress.Any, 50000);

})
[...]
```

Internally, the silo will listen on `0.0.0.0:40000` and `0.0.0.0:50000`, but the value published in the membership provider will be `172.16.0.42:11111` and `172.16.0.42:30000`.

## Application parts

``` csharp
    [...]
    // Application parts: just reference one of the grain implementations that we use
    .ConfigureApplicationParts(parts => parts.AddApplicationPart(typeof(ValueGrain).Assembly).WithReferences())
    [...];
```

Although this step is not technically required (if not configured, Orleans will scan all assemblies in the current folder), developers are encouraged to configure this.
This step will help Orleans to load user assemblies and types.
These assemblies are referred to as Application Parts.
All Grains, Grain Interfaces, and Serializers are discovered using Application Parts.

Application Parts are configured using `IApplicationPartsManager`, which can be accessed using the `ConfigureApplicationParts` extension method on `IClientBuilder` and `ISiloHostBuilder`.
The `ConfigureApplicationParts` method accepts a delegate, `Action<IApplicationPartManager>`.

The following extension methods on `IApplicationPartManager` support common uses:

* `AddApplicationPart(assembly)` a single assembly can be added using this extension method.
* `AddFromAppDomain()` adds all assemblies currently loaded in the `AppDomain`.
* `AddFromApplicationBaseDirectory()` loads and adds all assemblies in the current base path (see `AppDomain.BaseDirectory`).

Assemblies added by the above methods can be supplemented using the following extension methods on their return type, `IApplicationPartManagerWithAssemblies`:

* `WithReferences()` adds all referenced assemblies from the added parts. This immediately loads any transitively referenced assemblies. Assembly loading errors are ignored.
* `WithCodeGeneration()` generates support code for the added parts and adds it to the part manager. Note that this requires the `Microsoft.Orleans.OrleansCodeGenerator` package to be installed and is commonly referred to as runtime code generation.

Type discovery requires that the provided Application Parts include specific attributes.
Adding the build-time code generation package (`Microsoft.Orleans.CodeGenerator.MSBuild` or `Microsoft.Orleans.OrleansCodeGenerator.Build`) to each project containing Grains, Grain Interfaces, or Serializers is the recommended approach for ensuring that these attributes are present.
Build-time code generation only supports C#.
For F#, Visual Basic, and other .NET languages, code can be generated during configuration time via the `WithCodeGeneration()` method described above. More info regarding code generation could be found in [the corresponding section](../../core_concepts/code_generation.md).
