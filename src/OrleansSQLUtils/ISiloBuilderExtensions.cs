using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Extensions.Configuration;
using Orleans.Hosting;
using OrleansSQLUtils.Configuration;

namespace OrleansSQLUtils
{
    public static class ISiloBuilderExtensions
    {
        /// <summary>
        /// Configure SiloHostBuilder with SqlMembershipTable
        /// </summary>
        /// <param name="builder"></param>
        /// <param name="configureOptions"></param>
        /// <returns></returns>
        public static ISiloHostBuilder UseSqlMembershipTable(this ISiloHostBuilder builder,
            Action<SqlMembershipTableOptions> configureOptions)
        {
            return builder.ConfigureServices(services => services.UseSqlMembershipTable(configureOptions));
        }

        /// <summary>
        /// Configure SiloHostBuilder with SqlMembershipTable
        /// </summary>
        /// <param name="builder"></param>
        /// <param name="configureOptions"></param>
        /// <returns></returns>
        public static ISiloHostBuilder UseSqlMembershipTable(this ISiloHostBuilder builder,
            IConfiguration configuration)
        {
            return builder.ConfigureServices(services => services.UseSqlMembershipTable(configuration));
        }

        /// <summary>
        /// Configure SiloHostBuilder with SqlMembershipTable, and get its configuration from GlobalConfiguration
        /// </summary>
        /// <param name="builder"></param>
        /// <param name="configureOptions"></param>
        /// <returns></returns>
        public static ISiloHostBuilder UseSqlMembershipFromLegacyConfigurationSupport(this ISiloHostBuilder builder)
        {
            return builder.ConfigureServices(services => services.UseSqlMembershipFromLegacyConfigurationSupport());
        }
    }
}
