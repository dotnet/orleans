---
layout: page
title: Service Fabric Hosting
---
# Service Fabric Hosting

Orleans can be hosted on Service Fabric using the `Microsoft.Orleans.Hosting.ServiceFabric` package.
Silos should be hosted as unpartitioned, stateless services since Orleans manages distribution of grains itself using fine-grained, dynamic distribution. Other hosting options (partitioned, stateful) are currently untested and unsupported.

A sample which demonstrates hosting on Service Fabric is available at [Samples/2.0/ServiceFabric](https://github.com/dotnet/orleans/tree/master/Samples/2.0/ServiceFabric).

Hosting support is available in the `Microsoft.Orleans.Hosting.ServiceFabric` package. It allows an Orleans Silo to run as a Service Fabric `ICommunicationListener`. The Silo lifecycle follows the typical communication listener lifecycle: it is initialized via the `ICommunicationListener.OpenAsync` method and is gracefully terminated via the `ICommunicationListener.CloseAsync` method or abruptly terminated via the `ICommunicationListener.Abort` method.

`OrleansCommunicationListener` provides the `ICommunicationListener` implementation. The recommended approach is to create the communication listener using `OrleansServiceListener.CreateStateless(Action<StatelessServiceContext, ISiloHostBuilder> configure)` in the `Orleans.Hosting.ServiceFabric` namespace.

Each time the communication listener is opened, the `configure` delegate passed to `CreateStateless` is invoked to configure the new Silo.

## Example: Configuring Service Fabric hosting

The following example demonstrates a Service Fabric `StatelessService` class which hosts an Orleans silo. The full sample can be found in the [Samples/2.0/ServiceFabric](https://github.com/dotnet/orleans/tree/master/Samples/2.0/ServiceFabric) directory of the Orleans repository.

```csharp
/// <summary>
/// An instance of this class is created for each service instance by the Service Fabric runtime.
/// </summary>
internal sealed class StatelessCalculatorService : StatelessService
{
    public StatelessCalculatorService(StatelessServiceContext context)
        : base(context)
    {
    }

    /// <summary>
    /// Optional override to create listeners (e.g., TCP, HTTP) for this service replica to handle
    /// client or user requests.
    /// </summary>
    /// <returns>A collection of listeners.</returns>
    protected override IEnumerable<ServiceInstanceListener> CreateServiceInstanceListeners()
    {
        // Listeners can be opened and closed multiple times over the lifetime of a service instance.
        // A new Orleans silo will be both created and initialized each time the listener is opened
        // and will be shutdown when the listener is closed.
        var listener = OrleansServiceListener.CreateStateless(
            (fabricServiceContext, builder) =>
            {
                builder.Configure<ClusterOptions>(options =>
                {
                    // The service id is unique for the entire service over its lifetime. This is
                    // used to identify persistent state such as reminders and grain state.
                    options.ServiceId = fabricServiceContext.ServiceName.ToString();

                    // The cluster id identifies a deployed cluster. Since Service Fabric uses rolling
                    // upgrades, the cluster id can be kept constant. This is used to identify which
                    // silos belong to a particular cluster.
                    options.ClusterId = "development";
                });

                // Configure clustering. Other clustering providers are available, but for the purpose
                // of this sample we will use Azure Storage.
                // TODO: Pick a clustering provider and configure it here.
                builder.UseAzureStorageClustering(
                    options => options.ConnectionString = "UseDevelopmentStorage=true");

                // Optional: configure logging.
                builder.ConfigureLogging(logging => logging.AddDebug());

                builder.AddStartupTask<StartupTask>();

                // Service Fabric manages port allocations, so update the configuration using those
                // ports.
                // Gather configuration from Service Fabric.
                var activation = fabricServiceContext.CodePackageActivationContext;
                var endpoints = activation.GetEndpoints();

                // These endpoint names correspond to TCP endpoints specified in ServiceManifest.xml
                var siloEndpoint = endpoints["OrleansSiloEndpoint"];
                var gatewayEndpoint = endpoints["OrleansProxyEndpoint"];
                var hostname = fabricServiceContext.NodeContext.IPAddressOrFQDN;
                builder.ConfigureEndpoints(hostname, siloEndpoint.Port, gatewayEndpoint.Port);

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

    /// <summary>
    /// This is the main entry point for your service instance.
    /// </summary>
    /// <param name="cancellationToken">
    /// Canceled when Service Fabric needs to shut down this service instance.
    /// </param>
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
