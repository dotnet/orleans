using System;
using System.Collections.Generic;
using System.Fabric;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.ServiceFabric.Services.Communication.Runtime;
using Microsoft.ServiceFabric.Services.Runtime;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Hosting.ServiceFabric;
using Orleans.Runtime;

namespace Orleans.Clustering.ServiceFabric.Stateful
{
    public class ActorService : StatefulService
    {
        private readonly TaskCompletionSource<IClusterClient> clientPromise = new TaskCompletionSource<IClusterClient>();

        protected ActorService(StatefulServiceContext context) : base(context)
        {
        }

        public static StatefulService Create(
            StatefulServiceContext context,
            Action<ActorService, ISiloHostBuilder> configure = null,
            Func<ActorService, CancellationToken, Task> runAsync = null) =>
            new DelegateGrainService(context, configure, runAsync);

        public Task<IClusterClient> GetClient() => clientPromise.Task;

        public new IStatefulServicePartition Partition => base.Partition;

        protected sealed override IEnumerable<ServiceReplicaListener> CreateServiceReplicaListeners()
        {
            var siloListener = OrleansServiceListener.CreateStateful((context, builder) =>
                {
                    var serviceId = context.ServiceName.ToString();
                    var clusterId = "fabric";
                    var siloEndpointName = "OrleansSiloEndpoint";
                    var gatewayEndpointName = "OrleansProxyEndpoint";
                    var names = context.CodePackageActivationContext.GetConfigurationPackageNames();
                    if (names.Contains("Config"))
                    {
                        var config = context.CodePackageActivationContext.GetConfigurationPackageObject("Config");
                        var section = config.Settings.Sections.FirstOrDefault(s => string.Equals(s.Name, "Silo"))?.Parameters;
                        if (section != null)
                        {
                            {
                                var p = section.FirstOrDefault(s => string.Equals(s.Name, "ServiceId"))?.Value;
                                if (!string.IsNullOrWhiteSpace(p)) serviceId = p;
                            }

                            {
                                var p = section.FirstOrDefault(s => string.Equals(s.Name, "ClusterId"))?.Value;
                                if (!string.IsNullOrWhiteSpace(p)) clusterId = p;
                            }

                            {
                                var p = section.FirstOrDefault(s => string.Equals(s.Name, "SiloEndpointName"))?.Value;
                                if (!string.IsNullOrWhiteSpace(p)) siloEndpointName = p;
                            }

                            {
                                var p = section.FirstOrDefault(s => string.Equals(s.Name, "GatewayEndpointName"))?.Value;
                                if (!string.IsNullOrWhiteSpace(p)) gatewayEndpointName = p;
                            }
                        }
                    }

                    builder.Configure<ClusterOptions>(options =>
                    {
                        // The service id is unique for the entire service over its lifetime. This is used to identify persistent state
                        // such as reminders and grain state.
                        options.ServiceId = serviceId;

                        // The cluster id identifies a deployed cluster. Since Service Fabric uses rolling upgrades, the cluster id
                        // can be kept constant. This is used to identify which silos belong to a particular cluster.
                        options.ClusterId = clusterId;
                    });

                    // Use stateful service placement as the default placement strategy.
                    builder.ConfigureServices(services =>
                    {
                        services.RemoveAll<PlacementStrategy>();
                        services.AddSingleton<PlacementStrategy, StatefulServicePlacement>();
                    });

                    // Service Fabric manages port allocations, so update the configuration using those ports.
                    // Gather configuration from Service Fabric.
                    var activation = context.CodePackageActivationContext;
                    var endpoints = activation.GetEndpoints();

                    // These endpoint names correspond to TCP endpoints specified in ServiceManifest.xml
                    var siloEndpoint = endpoints[siloEndpointName];
                    var gatewayEndpoint = endpoints[gatewayEndpointName];
                    var hostname = context.NodeContext.IPAddressOrFQDN;
                    builder.ConfigureEndpoints(hostname, siloEndpoint.Port, gatewayEndpoint.Port, listenOnAnyHostAddress: true);

                    // Use Service Fabric for cluster membership, this means that no pings will be sent between silos and
                    // Orleans will use 'logical' silo addresses internally, only converting to physical IP addresses at the socket layer.
                    builder.UseServiceFabricClustering(context);

                    // So that we can call into grains from RunAsync without messing about with TaskScheduler or ClientBuilder.
                    builder.EnableDirectClient();

                    this.Configure(builder);
                },
                //this.WaitForWritable,
                onOpened: (host, _) => Task.FromResult(this.clientPromise.TrySetResult(host.Services.GetRequiredService<IClusterClient>())));

            return new[] {siloListener}.Concat(this.CreateAdditionalServiceReplicaListeners());
        }

        protected virtual void Configure(ISiloHostBuilder builder)
        {
        }

        protected virtual IEnumerable<ServiceReplicaListener> CreateAdditionalServiceReplicaListeners() => Array.Empty<ServiceReplicaListener>();

        private class DelegateGrainService : ActorService
        {
            private readonly Action<ActorService, ISiloHostBuilder> configure;
            private readonly Func<ActorService, CancellationToken, Task> runAsync;

            public DelegateGrainService(StatefulServiceContext context,
                Action<ActorService, ISiloHostBuilder> configure = null,
                Func<ActorService, CancellationToken, Task> runAsync = null) : base(context)
            {
                this.configure = configure;
                this.runAsync = runAsync;
            }

            protected override void Configure(ISiloHostBuilder builder)
            {
                base.Configure(builder);
                this.configure?.Invoke(this, builder);
            }

            protected override Task RunAsync(CancellationToken cancellationToken)
            {
                return this.runAsync?.Invoke(this, cancellationToken) ?? base.RunAsync(cancellationToken);
            }
        }
    }
}