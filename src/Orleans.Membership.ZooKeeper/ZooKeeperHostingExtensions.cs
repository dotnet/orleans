using System;
using Microsoft.Extensions.Configuration;
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
        /// Configure siloHostBuilder with ZooKeeperMembership
        /// </summary>
        /// <param name="builder"></param>
        /// <param name="configureOptions"></param>
        /// <returns></returns>
        public static ISiloHostBuilder UseZooKeeperMembership(this ISiloHostBuilder builder,
            Action<ZooKeeperMembershipOptions> configureOptions)
        {
            return builder.ConfigureServices(services => services.UseZooKeeperMembership(configureOptions));
        }

        /// <summary>
        /// Configure siloHostBuilder with ZooKeeperMembership
        /// </summary>
        /// <param name="builder"></param>
        /// <param name="configuration"></param>
        /// <returns></returns>
        public static ISiloHostBuilder UseZooKeeperMembership(this ISiloHostBuilder builder,
            IConfiguration configuration)
        {
            return builder.ConfigureServices(services => services.UseZooKeeperMembership(configuration));
        }

        /// <summary>
        /// Configure the client to use ZooKeeperGatewayListProvider
        /// </summary>
        /// <param name="builder"></param>
        /// <param name="configureOptions"></param>
        /// <returns></returns>
        public static IClientBuilder UseZooKeeperGatewayListProvider(this IClientBuilder builder,
            Action<ZooKeeperGatewayListProviderOptions> configureOptions)
        {
            return builder.ConfigureServices(services => services.UseZooKeeperGatewayListProvider(configureOptions));
        }

        /// <summary>
        /// Configure the client to use ZooKeeperGatewayListProvider
        /// </summary>
        /// <param name="builder"></param>
        /// <param name="configuration"></param>
        /// <returns></returns>
        public static IClientBuilder UseZooKeeperGatewayListProvider(this IClientBuilder builder,
            IConfiguration configuration)
        {
            return builder.ConfigureServices(services => services.UseZooKeeperGatewayListProvider(configuration));
        }

        /// <summary>
        /// Configure DI container with ZooKeeperMemebership
        /// </summary>
        /// <param name="services"></param>
        /// <param name="configureOptions"></param>
        /// <returns></returns>
        public static IServiceCollection UseZooKeeperMembership(this IServiceCollection services,
            Action<ZooKeeperMembershipOptions> configureOptions)
        {
            return services.Configure<ZooKeeperMembershipOptions>(configureOptions)
                .AddSingleton<IMembershipTable, ZooKeeperBasedMembershipTable>();
        }

        /// <summary>
        /// Configure DI container with ZooKeeperMemebership
        /// </summary>
        /// <param name="services"></param>
        /// <param name="configuration"></param>
        /// <returns></returns>
        public static IServiceCollection UseZooKeeperMembership(this IServiceCollection services,
             IConfiguration configuration)
        {
            return services.Configure<ZooKeeperMembershipOptions>(configuration)
                .AddSingleton<IMembershipTable, ZooKeeperBasedMembershipTable>();
        }

        /// <summary>
        /// Configure DI container with ZooKeeperGatewayListProvider
        /// </summary>
        /// <param name="services"></param>
        /// <param name="configureOptions"></param>
        /// <returns></returns>
        public static IServiceCollection UseZooKeeperGatewayListProvider(this IServiceCollection services,
            Action<ZooKeeperGatewayListProviderOptions> configureOptions)
        {
            return services.Configure(configureOptions)
                .AddSingleton<IGatewayListProvider, ZooKeeperGatewayListProvider>();
        }

        /// <summary>
        /// Configure DI container with ZooKeeperGatewayListProvider
        /// </summary>
        /// <param name="services"></param>
        /// <param name="configuration"></param>
        /// <returns></returns>
        public static IServiceCollection UseZooKeeperGatewayListProvider(this IServiceCollection services,
            IConfiguration configuration)
        {
            return services.Configure<ZooKeeperGatewayListProviderOptions>(configuration)
                .AddSingleton<IGatewayListProvider, ZooKeeperGatewayListProvider>();
        }
    }
}
