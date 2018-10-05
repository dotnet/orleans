---
layout: page
title: Client Configuration
---

> [!NOTE]
> If you just want to start a local silo and a local client for development purpose, look at the Local Development Configuration page.

# Client Configuration

A client for connecting to a cluster of silos and sending requests to grains is configured programmatically via a `ClientBuilder` and a number of supplemental option classes.
Like silo options, client option classes follow the [ASP.NET Options](https://docs.microsoft.com/en-us/aspnet/core/fundamentals/configuration/options).

There are several key aspects of client configuration:

* Orleans clustering information
* Clustering provider
* Application parts

Example of a client configuration:

``` csharp
var client = new ClientBuilder()
    // Clustering information
    .Configure<ClusterOptions>(options =>
    {
        options.ClusterId = "my-first-cluster";
        options.ServiceId = "MyAwesomeOrleansService";
    })
    // Clustering provider
    .UseAzureStorageClustering(options => options.ConnectionString = connectionString)
    // Application parts: just reference one of the grain interfaces that we use
    .ConfigureApplicationParts(parts => parts.AddApplicationPart(typeof(IValueGrain).Assembly))
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

Here we set two things:
- the `ClusterId` to `"my-first-cluster"`: this is a unique ID for the Orleans cluster. All clients and silo that uses this ID will be able to directly talk to each other. Some will choose to use a different `ClusterId` for each deployments for example.
- the `ServiceId` to `"AspNetSampleApp"`: this is a unique ID for your application, that will be used by some provider (for example for persistence providers). **This ID should be stable (not change) across deployments**.

## Clustering provider

``` csharp
    [...]
    // Clustering provider
    .UseAzureStorageClustering(options => options.ConnectionString = connectionString)
    [...]
```

The client will discover all gateway available in the cluster using this provider. Several providers are available, here in this sample we use the Azure Table provider.

To get more detail, look in the matching section in the Server Configuration page.

## Application parts

``` csharp
    [...]
    // Application parts: just reference one of the grain interfaces that we use
    .ConfigureApplicationParts(parts => parts.AddApplicationPart(typeof(IValueGrain).Assembly)).WithReferences())
    [...];
```

To get more detail, look in the matching section in the Server Configuration page.

