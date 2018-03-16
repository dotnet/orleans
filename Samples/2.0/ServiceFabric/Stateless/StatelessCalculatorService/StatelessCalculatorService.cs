using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Fabric;
using System.Fabric.Description;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Grains;
using Microsoft.Extensions.Logging;
using Microsoft.ServiceFabric.Services.Communication.Runtime;
using Microsoft.ServiceFabric.Services.Runtime;
using Orleans.Clustering.ServiceFabric;
using Orleans;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Hosting.ServiceFabric;
using Orleans.ServiceFabric;

namespace StatelessCalculatorService
{
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
        /// Optional override to create listeners (e.g., TCP, HTTP) for this service replica to handle client or user requests.
        /// </summary>
        /// <returns>A collection of listeners.</returns>
        protected override IEnumerable<ServiceInstanceListener> CreateServiceInstanceListeners()
        {
            // Listeners can be opened and closed multiple times over the lifetime of a service instance.
            // A new Orleans silo will be both created and initialized each time the listener is opened and will be shutdown 
            // when the listener is closed.
            var listener = OrleansServiceListener.CreateStateless(
                (serviceContext, builder) =>
                {
                    builder.Configure<ClusterOptions>(options =>
                    {
                        options.ServiceId = Guid.Empty;
                        options.ClusterId = "dev";
                    });

                    // Optional: use Service Fabric for cluster membership.
                    builder.UseServiceFabricClustering(serviceContext);
                    
                    // Optional: configure logging.
                    builder.ConfigureLogging(logging => logging.AddDebug());

                    builder.AddStartupTask<StartupTask>();
                    builder.UseInMemoryReminderService();

                    // Service Fabric manages port allocations, so update the configuration using those ports.
                    builder.Configure<EndpointOptions>(options =>
                    {
                        // Gather configuration from Service Fabric.
                        var activation = serviceContext.CodePackageActivationContext;
                        var endpoints = activation.GetEndpoints();

                        var siloEndpoint = endpoints[ServiceFabricConstants.SiloEndpointName];
                        var gatewayEndpoint = endpoints[ServiceFabricConstants.GatewayEndpointName];
                        var hostname = serviceContext.NodeContext.IPAddressOrFQDN;
                        builder.ConfigureEndpoints(hostname, siloEndpoint.Port, gatewayEndpoint.Port);
                    });

                    // Add your application assemblies.
                    builder.ConfigureApplicationParts(parts =>
                    {
                        parts.AddApplicationPart(typeof(CalculatorGrain).Assembly).WithReferences();
                        
                        // Alternative: add all loadable assemblies in the current base path (see AppDomain.BaseDirectory).
                        parts.AddFromApplicationBaseDirectory();
                    });
                });

            return new[] { listener };
        }

        /// <summary>
        /// This is the main entry point for your service instance.
        /// </summary>
        /// <param name="cancellationToken">Canceled when Service Fabric needs to shut down this service instance.</param>
        protected override async Task RunAsync(CancellationToken cancellationToken)
        {
            // TODO: Replace the following sample code with your own logic 
            //       or remove this RunAsync override if it's not needed in your service.
            
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
            }
        }
    }
}
