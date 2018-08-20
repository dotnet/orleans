using System;
using System.Fabric;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.ServiceFabric.Data;
using Microsoft.ServiceFabric.Services.Client;
using Orleans.Clustering.ServiceFabric.Stateful;
using Orleans.Clustering.ServiceFabric.Utilities;
using Orleans.Hosting;
using Orleans.Messaging;
using Orleans.Providers;
using Orleans.Runtime;
using Orleans.Storage;

namespace Orleans.Clustering.ServiceFabric
{
    /// <summary>
    /// Extensions for hosting Orleans on Service Fabric.
    /// </summary>
    public static class OrleansServiceFabricExtensions
    {
        /// <summary>
        /// Adds Service Fabric cluster membership support.
        /// </summary>
        /// <param name="builder">The host builder.</param>
        /// <param name="serviceContext">The Service Fabric service context.</param>
        /// <returns>The provided service collection.</returns>
        public static ISiloHostBuilder UseServiceFabricClustering(
            this ISiloHostBuilder builder,
            ServiceContext serviceContext)
        {
            return builder.ConfigureServices(serviceCollection => serviceCollection.UseServiceFabricClustering(serviceContext));
        }

        /// <summary>
        /// Adds Service Fabric cluster membership support.
        /// </summary>
        /// <param name="serviceCollection">The service collection.</param>
        /// <param name="serviceContext">The Service Fabric service context.</param>
        /// <returns>The provided service collection.</returns>
        public static IServiceCollection UseServiceFabricClustering(
            this IServiceCollection serviceCollection,
            ServiceContext serviceContext)
        {
            AddStandardServices(serviceCollection);
            AddSiloServices(serviceCollection, serviceContext);

            return serviceCollection;
        }

        /// <summary>
        /// Adds support for connecting to a cluster hosted in Service Fabric.
        /// </summary>
        /// <param name="clientBuilder">The client builder.</param>
        /// <param name="serviceName">The Service Fabric service name.</param>
        /// <returns>The provided client builder.</returns>
        public static IClientBuilder UseServiceFabricClustering(
            this IClientBuilder clientBuilder,
            string serviceName)
        {
            return clientBuilder.UseServiceFabricClustering(new Uri(serviceName));
        }

        /// <summary>
        /// Adds support for connecting to a cluster hosted in Service Fabric.
        /// </summary>
        /// <param name="clientBuilder">The client builder.</param>
        /// <param name="serviceName">The Service Fabric service name.</param>
        /// <returns>The provided client builder.</returns>
        public static IClientBuilder UseServiceFabricClustering(
            this IClientBuilder clientBuilder,
            Uri serviceName)
        {
            clientBuilder.ConfigureServices(
                serviceCollection =>
                {
                    AddStandardServices(serviceCollection);

                    // Use Service Fabric for cluster membership.
                    serviceCollection.TryAddSingleton<IFabricServiceSiloResolver>(sp => ActivatorUtilities.CreateInstance<FabricServiceSiloResolver>(sp, serviceName));

                    serviceCollection.TryAddSingleton<IGatewayListProvider, FabricGatewayProvider>();
                });

            return clientBuilder;
        }

        /// <summary>
        /// Adds services which are common between silos and clients.
        /// </summary>
        /// <param name="serviceCollection">The service collection.</param>
        private static void AddStandardServices(IServiceCollection serviceCollection)
        {
            serviceCollection.TryAddSingleton<FabricClient>();
            serviceCollection.TryAddSingleton<CreateFabricClientDelegate>(sp => () => sp.GetRequiredService<FabricClient>());
            serviceCollection.TryAddSingleton<IServicePartitionResolver, ServicePartitionResolver>();
            serviceCollection.TryAddSingleton<IFabricQueryManager, FabricQueryManager>();
        }

        /// <summary>
        /// Adds services which are required by Service Fabric silos.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <param name="context">The service context.</param>
        private static void AddSiloServices(IServiceCollection services, ServiceContext context)
        {
            // Use Service Fabric for cluster membership.
            services.TryAddSingleton<IFabricServiceSiloResolver>(
                    sp => ActivatorUtilities.CreateInstance<FabricServiceSiloResolver>(sp, context.ServiceName));
            services.TryAddSingleton<ISiloStatusOracle>(provider => provider.GetService<IMembershipOracle>());
            services.TryAddSingleton<ServiceContext>(context);

            if (context is StatefulServiceContext stateful)
            {
                services.TryAddSingleton<StatefulServiceContext>(stateful);
                services.TryAddSingleton<IMembershipOracle, FabricMembershipOracle>();
                services.TryAddSingleton<UnknownSiloMonitor>();
                services.AddPlacementDirector<StatefulServicePlacement, StatefulServicePlacementDirector>();
            }
            else if (context is StatelessServiceContext stateless)
            {
                services.TryAddSingleton<StatelessServiceContext>(stateless);
                services.TryAddSingleton<IMembershipOracle, FabricMembershipOracle>();
                services.TryAddSingleton<UnknownSiloMonitor>();
            }
            else
            {
                throw new NotSupportedException($"Services with context of type {context.GetType()} are not supported.");
            }
        }

        public static ISiloHostBuilder AddReliableDictionaryGrainStorage(this ISiloHostBuilder builder, IReliableStateManager stateManager, string name = null)
        {
            builder.ConfigureServices(services =>
            {
                name = name ?? ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME;
                if (name != null)
                {
                    services.AddSingletonNamedService<IGrainStorage>(
                            name,
                            (sp, n) => ActivatorUtilities.CreateInstance<ReliableDictionaryGrainStorage>(sp, name, stateManager));
                }
            });

            return builder;
        }
    }
}