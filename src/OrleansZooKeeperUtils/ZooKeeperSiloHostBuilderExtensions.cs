using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Extensions.Configuration;
using Orleans.Hosting;
using OrleansZooKeeperUtils.Configuration;

namespace Microsoft.Orleans.Hosting
{
    public static class ZooKeeperSiloHostBuilderExtensions
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
    }
}
 