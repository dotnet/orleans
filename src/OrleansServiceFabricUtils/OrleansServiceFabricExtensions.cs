using System;
using System.Fabric;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.ServiceFabric.Services.Runtime;

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
                        sp.GetService<Func<string, Logger>>()));
            serviceCollection.AddSingleton<IMembershipOracle, FabricMembershipOracle>();
            serviceCollection.AddSingleton<IGatewayListProvider, FabricGatewayProvider>();

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
                        sp.GetService<Func<string, Logger>>()));
            serviceCollection.AddSingleton<IMembershipOracle, FabricMembershipOracle>();
            serviceCollection.AddSingleton<IGatewayListProvider, FabricGatewayProvider>();
            serviceCollection.AddTransient<ServiceContext>(_ => service.Context);

            return serviceCollection;
        }

        /// <summary>
        /// Adds support for connecting to a cluster hosted in Service Fabric to the provided service collection.
        /// </summary>
        /// <param name="serviceCollection">The service collection.</param>
        /// <param name="serviceName">The Service Fabric service name.</param>
        /// <returns>The provided service collection.</returns>
        public static IServiceCollection AddServiceFabricClientSupport(
            this IServiceCollection serviceCollection,
            Uri serviceName)
        {
            AddStandardServices(serviceCollection);

            // Use Service Fabric for cluster membership.
            serviceCollection.AddSingleton<IFabricServiceSiloResolver>(
                sp =>
                new FabricServiceSiloResolver(
                    serviceName,
                    sp.GetService<IFabricQueryManager>()));
            serviceCollection.AddSingleton<IMembershipOracle, FabricMembershipOracle>();
            return serviceCollection;
        }

        private static void AddStandardServices(IServiceCollection serviceCollection)
        {
            serviceCollection.TryAddSingleton<FabricClient>();
            serviceCollection.TryAddSingleton<CreateFabricClientDelegate>(sp => () => sp.GetService<FabricClient>());
            serviceCollection.TryAddSingleton<IServicePartitionResolver, ServicePartitionResolver>();
            serviceCollection.TryAddSingleton<IFabricQueryManager, FabricQueryManager>();
        }
    }
}