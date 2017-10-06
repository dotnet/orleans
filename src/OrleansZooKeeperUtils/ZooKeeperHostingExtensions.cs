using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime.Membership;
using OrleansZooKeeperUtils.Configuration;

namespace Orleans.Hosting
{
    public static class ZooKeeperHostingExtensions
    {
        /// <summary>
        /// Configure siloHostBuilder with ZooKeeperMembershipTable
        /// </summary>
        /// <param name="builder"></param>
        /// <param name="configureOptions"></param>
        /// <returns></returns>
        public static ISiloHostBuilder UseZooKeeperMembershipTable(this ISiloHostBuilder builder,
            Action<ZooKeeperMembershipOptions> configureOptions)
        {
            return builder.ConfigureServices(services => services.UseZooKeeperMembership(configureOptions));
        }

        /// <summary>
        /// Configure siloHostBuilder with ZooKeeperMembershipTable
        /// </summary>
        /// <param name="builder"></param>
        /// <param name="configuration"></param>
        /// <returns></returns>
        public static ISiloHostBuilder UseZooKeeperMembershipTable(this ISiloHostBuilder builder,
            IConfiguration configuration)
        {
            return builder.ConfigureServices(services => services.UseZooKeeperMembership(configuration));
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
    }
}
