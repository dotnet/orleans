using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Extensions.Configuration;
using Orleans.Hosting;
using OrleansZooKeeperUtils.Configuration;

namespace OrleansZooKeeperUtils
{
    public static class ISiloHostBuilderExtensions
    {
        /// <summary>
        /// Configure siloHostBuilder with ZooKeeperMembershipTable
        /// </summary>
        /// <param name="builder"></param>
        /// <param name="configureOptions"></param>
        /// <returns></returns>
        public static ISiloHostBuilder UseZooKeeperMembershipTable(this ISiloHostBuilder builder,
            Action<ZooKeeperMembershipTableOptions> configureOptions)
        {
            return builder.ConfigureServices(services => services.UseZooKeeperMembershipTable(configureOptions));
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
            return builder.ConfigureServices(services => services.UseZooKeeperMembershipTable(configuration));
        }

        /// <summary>
        /// Configure siloHostBuilder with ZooKeeperMembershipTable, and get its configuration from GlobalConfiguration
        /// </summary>
        /// <param name="builder"></param>
        /// <returns></returns>
        public static ISiloHostBuilder UseZooKeeperMembershipTableFromLegacyConfigurationSupport(this ISiloHostBuilder builder)
        {
            return builder.ConfigureServices(services => services.UseZooKeeperMembershipTableFromLegacyConfigurationSupport());
        }
    }
}
 