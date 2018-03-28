using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Fabric;
using System.Threading;
using System.Threading.Tasks;
using Grains;
using Microsoft.Extensions.Logging;
using Microsoft.ServiceFabric.Services.Communication.Runtime;
using Microsoft.ServiceFabric.Services.Runtime;
using Orleans;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Hosting.ServiceFabric;

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
                (fabricServiceContext, builder) =>
                {
                    builder.Configure<ClusterOptions>(options =>
                    {
                        // The service id is unique for the entire service over its lifetime. This is used to identify persistent state
                        // such as reminders and grain state.
                        options.ServiceId = fabricServiceContext.ServiceName.ToString();

                        // The cluster id identifies a deployed cluster. Since Service Fabric uses rolling upgrades, the cluster id
                        // can be kept constant. This is used to identify which silos belong to a particular cluster.
                        options.ClusterId = "development";
                    });

                    // Configure clustering. Other clustering providers are available, but for the purpose of this sample we
                    // will use Azure Storage.
                    // TODO: Pick a clustering provider and configure it here.
                    builder.UseAzureStorageClustering(options => options.ConnectionString = "UseDevelopmentStorage=true");
                    
                    // Optional: configure logging.
                    builder.ConfigureLogging(logging => logging.AddDebug());

                    builder.AddStartupTask<StartupTask>();

                    // Service Fabric manages port allocations, so update the configuration using those ports.
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
