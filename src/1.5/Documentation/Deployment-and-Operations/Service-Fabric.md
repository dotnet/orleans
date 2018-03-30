---
layout: page
title: Service Fabric Hosting
---

[!include[](../../warning-banner.md)]

# Service Fabric Hosting

## Overview

Orleans can be hosted on Service Fabric. There are currently two points of integration with Service Fabric:

* **Hosting**: Silos can be hosted on Service Fabric inside of a Service Fabric Reliable Service. Silos should be hosted as unpartitioned, stateless services since Orleans manages distribution of grains itself using fine-grained, dynamic distribution. Other hosting options (partitioned, stateful) are currently untested and unsupported.
* **Clustering** (beta): Silos and clients can leverage Service Fabric's Service Discovery mechanisms to form clusters. This option requires Service Fabric Hosting, however Service Fabric Hosting does not require Service Fabric Clustering.

A sample which demonstrates hosting and clustering is present at [Samples/ServiceFabric](https://github.com/dotnet/orleans/tree/master/Samples/ServiceFabric).

## Hosting

Hosting support is available in the `Microsoft.Orleans.Hosting.ServiceFabric` package. It allows an Orleans Silo to run as a Service Fabric `ICommunicationListener`. The Silo lifecycle follows the typical communication listener lifecycle: it is initialized via the `ICommunicationListener.OpenAsync` method and is gracefully terminated via the `ICommunicationListener.CloseAsync` method or abruptly terminated via the `ICommunicationListener.Abort` method.

`OrleansCommunicationListener` provides the `ICommunicationListener` implementation. The recommended approach is to create the communication listener using `OrleansServiceListener.CreateStateless(Action<StatelessServiceContext, ISiloHostBuilder> configure)` in the `Orleans.Hosting.ServiceFabric` namespace. This ensures that the listener has the endpoint name required by **Clustering** (described below).

Each time the communication listener is opened, the `configure` delegate passed to `CreateStateless` is invoked to configure the new Silo.

Hosting can be used in conjunction with the Service Fabric Clustering provider, however other clustering providers can be used instead.

### Example: Configuring Service Fabric hosting.

The following example demonstrates a Service Fabric `StatelessService` class which hosts an Orleans silo. The full sample can be found in the [Samples/ServiceFabric](https://github.com/dotnet/orleans/tree/master/Samples/ServiceFabric) directory of the Orleans repository.

```csharp
internal sealed class StatelessCalculatorService : StatelessService
{
    public StatelessCalculatorService(StatelessServiceContext context)
        : base(context)
    {
    }

    protected override IEnumerable<ServiceInstanceListener> CreateServiceInstanceListeners()
    {
        // Listeners can be opened and closed multiple times over the lifetime of a service
        // instance. A new Orleans silo will be both created and initialized each time the
        // listener is opened and will be shutdown when the listener is closed.
        var listener = OrleansServiceListener.CreateStateless(
            (serviceContext, builder) =>
            {
                // Optional: use Service Fabric for cluster membership.
                builder.UseServiceFabricClustering(serviceContext);

                // Alternative: use Azure Storage for cluster membership.
                builder.UseAzureTableMembership(options =>
                {
                    /* Configure connection string*/
                });

                // Optional: configure logging.
                builder.ConfigureLogging(logging => logging.AddDebug());

                var config = new ClusterConfiguration();
                config.Globals.RegisterBootstrapProvider<BootstrapProvider>("poke_grains");
                config.Globals.ReminderServiceType =
                    GlobalConfiguration.ReminderServiceProviderType.ReminderTableGrain;

                // Service Fabric manages port allocations, so update the configuration using
                // those ports.
                config.Defaults.ConfigureServiceFabricSiloEndpoints(serviceContext);

                // Tell Orleans to use this configuration.
                builder.UseConfiguration(config);

                // Add your application assemblies.
                builder.ConfigureApplicationParts(parts =>
                {
                    parts.AddApplicationPart(typeof(CalculatorGrain).Assembly).WithReferences();
                        
                    // Alternative: add all loadable assemblies in the current base path
                    // (see AppDomain.BaseDirectory).
                    parts.AddFromApplicationBaseDirectory();
                });
            });

        return new[] { listener };
    }

    protected override async Task RunAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
        }
    }
}
```

## Clustering (beta)

*Note: it is currently recommended to use a storage-backed clustering provider such as SQL, ZooKeeper, Consul, or Azure Tables in production while this feature is in beta.*

Support to use Service Fabric's Service Discovery (Naming Service) mechanism for cluster membership is available in the `Microsoft.Orleans.Clustering.ServiceFabric` package. The implementation requires that the Service Fabric **Hosting** support is also used and that the Silo endpoint is named "Orleans" in the value returned from `StatelessService.CreateServiceInstanceListeners()`. The simplest way to ensure this is to use the `OrleansServiceListener.CreateStateless(...)` method as described in the previous section.

Service Fabric Clustering is enabled with the `ISiloHostBuilder.UseServiceFabricClustering(ServiceContext)` extension method on the silo and the `IClientBuilder.UseServiceFabricClustering(Uri)` extension method on the client.

The current recommendation is to use a storage-backed clustering provider for production services, such as SQL, ZooKeeper, Consul, or Azure Storage. These providers (particularly SQL and Azure Storage) are sufficiently well tested for production use.