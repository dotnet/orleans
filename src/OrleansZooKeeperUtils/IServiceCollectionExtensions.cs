using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.Runtime.Configuration;
using Orleans.Runtime.Host;
using OrleansZooKeeperUtils.Configuration;

namespace OrleansZooKeeperUtils
{
    public static class IServiceCollectionExtensions
    {
        /// <summary>
        /// Configure DI container with ZooKeeperMemebershipTable
        /// </summary>
        /// <param name="services"></param>
        /// <param name="configureOptions"></param>
        /// <returns></returns>
        public static IServiceCollection UseZooKeeperMembershipTable(this IServiceCollection services,
            Action<ZooKeeperMembershipTableOptions> configureOptions)
        {
            return services.Configure<ZooKeeperMembershipTableOptions>(configureOptions)
                .AddSingleton<IMembershipTable, ZooKeeperBasedMembershipTable>();
        }

        /// <summary>
        /// Configure DI container with ZooKeeperMemebershipTable
        /// </summary>
        /// <param name="services"></param>
        /// <param name="configureOptions"></param>
        /// <returns></returns>
        public static IServiceCollection UseZooKeeperMembershipTable(this IServiceCollection services,
             IConfiguration configuration)
        {
            return services.Configure<ZooKeeperMembershipTableOptions>(configuration)
                .AddSingleton<IMembershipTable, ZooKeeperBasedMembershipTable>();
        }

        /// <summary>
        /// Configure DI container with ZooKeeperMemebershipTable, and get its configuration from <see cref="GlobalConfiguration"/>
        /// </summary>
        /// <param name="services"></param>
        /// <returns></returns>
        public static IServiceCollection UseZooKeeperMembershipTableFromLegacyConfigurationSupport(
            this IServiceCollection services)
        {
            services.Configure<GlobalConfiguration, ZooKeeperMembershipTableOptions>((configuration, options) =>
            {
                options.DataConnectionString = configuration.DataConnectionString;
                options.DeploymentId = configuration.DeploymentId;
            });
            return services;
        }

        private static void Configure<TConfiguration, TOptions>(this IServiceCollection services, Action<TConfiguration, TOptions> configureOptions) where TOptions : class
        {
            services.AddSingleton<IConfigureOptions<TOptions>>(serviceProvider =>
            {
                var configuration = serviceProvider.GetRequiredService<TConfiguration>();

                return new ConfigureOptions<TOptions>(options => configureOptions(configuration, options));
            });
        }
    }
}
