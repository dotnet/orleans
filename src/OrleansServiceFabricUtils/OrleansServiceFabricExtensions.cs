using System;
using System.Fabric;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.ServiceFabric.Services.Runtime;
using Orleans;
using Orleans.Messaging;
using Orleans.Runtime;

namespace Microsoft.Orleans.ServiceFabric
{
    using Microsoft.Orleans.ServiceFabric.Utilities;
    using Microsoft.ServiceFabric.Services.Client;

    /// <summary>
    /// Extensions for hosting Orleans on Service Fabric.
    /// </summary>
    public static class OrleansServiceFabricExtensions
    {
        /// <summary>
        /// Adds Service Fabric support to the provided service collection.
        /// </summary>
        /// <param name="serviceCollection">The service collection.</param>
        /// <param name="service">The Service Fabric service.</param>
        /// <returns>The provided service collection.</returns>
        public static IServiceCollection AddServiceFabricSupport(
            this IServiceCollection serviceCollection,
            StatefulService service)
        {
            AddStandardServices(serviceCollection);

            // Use Service Fabric for cluster membership.
            serviceCollection.AddSingleton<IFabricServiceSiloResolver>(
                sp =>
                    new FabricServiceSiloResolver(
                        service.Context.ServiceName,
                        sp.GetService<IFabricQueryManager>(),
                        sp.GetService<Factory<string, Logger>>()));
            serviceCollection.AddSingleton<IMembershipOracle, FabricMembershipOracle>();
            serviceCollection.AddSingleton<IGatewayListProvider, FabricGatewayProvider>();

            serviceCollection.AddSingleton<ISiloStatusOracle>(provider => provider.GetService<IMembershipOracle>());

            // In order to support local, replicated persistence, the state manager must be registered.
            serviceCollection.AddTransient(_ => service.StateManager);
            serviceCollection.AddTransient<ServiceContext>(_ => service.Context);

            return serviceCollection;
        }

        /// <summary>
        /// Adds Service Fabric support to the provided service collection.
        /// </summary>
        /// <param name="serviceCollection">The service collection.</param>
        /// <param name="service">The Service Fabric service.</param>
        /// <returns>The provided service collection.</returns>
        public static IServiceCollection AddServiceFabricSupport(
            this IServiceCollection serviceCollection,
            StatelessService service)
        {
            AddStandardServices(serviceCollection);

            // Use Service Fabric for cluster membership.
            serviceCollection.AddSingleton<IFabricServiceSiloResolver>(
                sp =>
                    new FabricServiceSiloResolver(
                        service.Context.ServiceName,
                        sp.GetService<IFabricQueryManager>(),
                        sp.GetService<Factory<string, Logger>>()));
            serviceCollection.AddSingleton<IMembershipOracle, FabricMembershipOracle>();

            serviceCollection.AddSingleton<ISiloStatusOracle>(provider => provider.GetService<IMembershipOracle>());

            serviceCollection.AddSingleton<IGatewayListProvider, FabricGatewayProvider>();
            serviceCollection.AddTransient<ServiceContext>(_ => service.Context);

            return serviceCollection;
        }

        /// <summary>
        /// Adds support for connecting to a cluster hosted in Service Fabric.
        /// </summary>
        /// <param name="clientBuilder">The client builder.</param>
        /// <param name="serviceName">The Service Fabric service name.</param>
        /// <returns>The provided client builder.</returns>
        public static IClientBuilder AddServiceFabric(
            this IClientBuilder clientBuilder,
            string serviceName)
        {
            return clientBuilder.AddServiceFabric(new Uri(serviceName));
        }

        /// <summary>
        /// Adds support for connecting to a cluster hosted in Service Fabric.
        /// </summary>
        /// <param name="clientBuilder">The client builder.</param>
        /// <param name="serviceName">The Service Fabric service name.</param>
        /// <returns>The provided client builder.</returns>
        public static IClientBuilder AddServiceFabric(
            this IClientBuilder clientBuilder,
            Uri serviceName)
        {
            clientBuilder.ConfigureServices(
                serviceCollection =>
                {
                    AddStandardServices(serviceCollection);

                    // Use Service Fabric for cluster membership.
                    serviceCollection.AddSingleton<IFabricServiceSiloResolver>(
                        sp =>
                            new FabricServiceSiloResolver(
                                serviceName,
                                sp.GetService<IFabricQueryManager>(),
                                sp.GetService<Factory<string, Logger>>()));
                    serviceCollection.AddSingleton<IGatewayListProvider, FabricGatewayProvider>();
                });

            return clientBuilder;
        }

        private static void AddStandardServices(IServiceCollection serviceCollection)
        {
            serviceCollection.AddSingleton<FabricClient>();
            serviceCollection.AddSingleton<CreateFabricClientDelegate>(sp => () => sp.GetRequiredService<FabricClient>());
            serviceCollection.AddSingleton<IServicePartitionResolver, ServicePartitionResolver>();
            serviceCollection.AddSingleton<IFabricQueryManager, FabricQueryManager>();
        }
    }
}