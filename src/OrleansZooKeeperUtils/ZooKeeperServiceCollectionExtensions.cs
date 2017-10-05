using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Runtime.Membership;
using OrleansZooKeeperUtils.Configuration;

namespace Orleans.Runtime.Hosting
{
    public static class ZooKeeperServiceCollectionExtensions
    {
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
