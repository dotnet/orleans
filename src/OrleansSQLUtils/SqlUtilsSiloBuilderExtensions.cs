using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Extensions.Configuration;
using Orleans.Hosting;
using OrleansSQLUtils.Configuration;

namespace Microsoft.Orleans.Hosting
{
    public static class SqlUtilsSiloBuilderExtensions
    {
        /// <summary>
        /// Configure SiloHostBuilder with SqlMembership
        /// </summary>
        /// <param name="builder"></param>
        /// <param name="configureOptions"></param>
        /// <returns></returns>
        public static ISiloHostBuilder UseSqlMembership(this ISiloHostBuilder builder,
            Action<SqlMembershipOptions> configureOptions)
        {
            return builder.ConfigureServices(services => services.UseSqlMembership(configureOptions));
        }

        /// <summary>
        /// Configure SiloHostBuilder with SqlMembership
        /// </summary>
        /// <param name="builder"></param>
        /// <param name="configuration"></param>
        /// <returns></returns>
        public static ISiloHostBuilder UseSqlMembership(this ISiloHostBuilder builder,
            IConfiguration configuration)
        {
            return builder.ConfigureServices(services => services.UseSqlMembership(configuration));
        }
    }
}
