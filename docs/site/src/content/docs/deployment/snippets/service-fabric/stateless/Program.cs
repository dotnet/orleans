using System.Fabric;
using Microsoft.Extensions.Hosting;
using Microsoft.ServiceFabric.Services.Runtime;
using ServiceFabric.HostingExample;

try
{
    // The ServiceManifest.XML file defines one or more service type names.
    // Registering a service maps a service type name to a .NET type.
    // When Service Fabric creates an instance of this service type,
    // an instance of the class is created in this host process.
    await ServiceRuntime.RegisterServiceAsync(
        "Orleans.ServiceFabric.Stateless",
        context => new OrleansHostedStatelessService(
            CreateHostAsync, context));

    ServiceEventSource.Current.ServiceTypeRegistered(
        Environment.ProcessId,
        typeof(OrleansHostedStatelessService).Name);

    // Prevents this host process from terminating so services keep running.
    await Task.Delay(Timeout.Infinite);
}
catch (Exception ex)
{
    ServiceEventSource.Current.ServiceHostInitializationFailed(
        ex.ToString());
    throw;
}

static async Task<IHost> CreateHostAsync(StatelessServiceContext context)
{
    await Task.CompletedTask;

    return Host.CreateDefaultBuilder()
        .UseOrleans((_, builder) =>
        {
            // TODO, Use real storage, something like table storage
            // or SQL Server for clustering.
            builder.UseLocalhostClustering();

            // Service Fabric manages port allocations, so update the 
            // configuration using those ports. Gather configuration from 
            // Service Fabric.
            var activation = context.CodePackageActivationContext;
            var endpoints = activation.GetEndpoints();

            // These endpoint names correspond to TCP endpoints 
            // specified in ServiceManifest.xml
            var siloEndpoint = endpoints["OrleansSiloEndpoint"];
            var gatewayEndpoint = endpoints["OrleansProxyEndpoint"];
            var hostname = context.NodeContext.IPAddressOrFQDN;
            builder.ConfigureEndpoints(hostname,
                siloEndpoint.Port, gatewayEndpoint.Port);
        })
        .Build();
}
