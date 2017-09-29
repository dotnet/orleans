using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Extensions.Configuration;
using Orleans.ConsulUtils.Configuration;
using Orleans.Hosting;

namespace OrleansConsulUtils
{
    public static class ISiloHostBuilderExtensions 
    {
        /// <summary>
        /// Configure siloHostBuilder to use ConsulMembershipTable
        /// </summary>
        /// <param name="builder"></param>
        /// <param name="configureOptions"></param>
        /// <returns></returns>
        public static ISiloHostBuilder UseConsulMembershipTable(this ISiloHostBuilder builder,
            Action<ConsulMembershipTableOptions> configureOptions)
        {
            return builder.ConfigureServices(services => services.UseConsulMembershipTable(configureOptions));
        }

        /// <summary>
        /// Configure siloHostBuilder to use ConsulMembershipTable
        /// </summary>
        /// <param name="builder"></param>
        /// <param name="configuration"></param>
        /// <returns></returns>
        public static ISiloHostBuilder UseConsulMembershipTable(this ISiloHostBuilder builder,
            IConfiguration configuration)
        {
            return builder.ConfigureServices(services => services.UseConsulMembershipTable(configuration));
        }

        /// <summary>
        /// Configure siloHostBuilder to use ConsulMembershipTable, and configure it from GlobalConfiguration
        /// </summary>
        /// <param name="builder"></param>
        /// <returns></returns>
        public static ISiloHostBuilder UseConsulMembershipTableFromLegacyConfigurationSupport(this ISiloHostBuilder builder)
        {
            return builder.ConfigureServices(services => services.UseConsulMembershipTableFromLegacyConfigurationSupport());
        }
    }
}
