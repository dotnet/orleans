using System;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Messaging;
using Orleans.Runtime.Membership;
using OrleansZooKeeperUtils.Configuration;
using OrleansZooKeeperUtils.Options;

namespace Orleans.Hosting
{
    public static class ZooKeeperHostingExtensions
    {
        /// <summary>
        /// Configures the silo to use ZooKeeper for cluster membership
        /// </summary>
        public static ISiloHostBuilder UseZooKeeperMembership(this ISiloHostBuilder builder,
            Action<ZooKeeperMembershipOptions> configureOptions)
        {
            return builder.ConfigureServices(services => services.UseZooKeeperMembership(configureOptions));
        }

        /// <summary>
        /// Configures the silo to use ZooKeeper for cluster membership
        /// </summary>
        public static ISiloHostBuilder UseZooKeeperMembership(this ISiloHostBuilder builder,
            Action<OptionsBuilder<ZooKeeperMembershipOptions>> configureOptions)
        {
            return builder.ConfigureServices(services => services.UseZooKeeperMembership(configureOptions));
        }

        /// <summary>
        /// Configure the client to use ZooKeeper as the Gateway List provider
        /// </summary>
        public static IClientBuilder UseZooKeeperGatewayListProvider(this IClientBuilder builder,
            Action<ZooKeeperGatewayListProviderOptions> configureOptions)
        {
            return builder.ConfigureServices(services => services.UseZooKeeperGatewayListProvider(configureOptions));
        }

        /// <summary>
        /// Configure the client to use ZooKeeper as the Gateway List provider
        /// </summary>
        public static IClientBuilder UseZooKeeperGatewayListProvider(this IClientBuilder builder,
            Action<OptionsBuilder<ZooKeeperGatewayListProviderOptions>> configureOptions)
        {
            return builder.ConfigureServices(services => services.UseZooKeeperGatewayListProvider(configureOptions));
        }

        /// <summary>
        /// Configure DI container with ZooKeeper based Membership
        /// </summary>
        public static IServiceCollection UseZooKeeperMembership(this IServiceCollection services,
            Action<ZooKeeperMembershipOptions> configureOptions)
        {
            return services.UseZooKeeperMembership(ob => ob.Configure(configureOptions));
        }

        /// <summary>
        /// Configure DI container with ZooKeeper based Membership
        /// </summary>
        public static IServiceCollection UseZooKeeperMembership(this IServiceCollection services,
            Action<OptionsBuilder<ZooKeeperMembershipOptions>> configureOptions)
        {
            configureOptions?.Invoke(services.AddOptions<ZooKeeperMembershipOptions>());
            return services.AddSingleton<IMembershipTable, ZooKeeperBasedMembershipTable>();
        }

        /// <summary>
        /// Configure DI container with ZooKeeperGatewayListProvider
        /// </summary>
        public static IServiceCollection UseZooKeeperGatewayListProvider(this IServiceCollection services,
            Action<ZooKeeperGatewayListProviderOptions> configureOptions)
        {
            return services.UseZooKeeperGatewayListProvider(ob => ob.Configure(configureOptions));
        }

        /// <summary>
        /// Configure DI container with ZooKeeperGatewayListProvider
        /// </summary>
        public static IServiceCollection UseZooKeeperGatewayListProvider(this IServiceCollection services,
            Action<OptionsBuilder<ZooKeeperGatewayListProviderOptions>> configureOptions)
        {
            configureOptions?.Invoke(services.AddOptions<ZooKeeperGatewayListProviderOptions>());
            return services.AddSingleton<IGatewayListProvider, ZooKeeperGatewayListProvider>();
        }
    }
}
