using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Extensions.Configuration;
using Orleans.ConsulUtils.Configuration;
using Orleans.Hosting;

namespace Microsoft.Orleans.Hosting
{
    public static class ConsulUtilsSiloHostBuilderExtensions 
    {
        /// <summary>
        /// Configure siloHostBuilder to use ConsulMembership
        /// </summary>
        /// <param name="builder"></param>
        /// <param name="configureOptions"></param>
        /// <returns></returns>
        public static ISiloHostBuilder UseConsulMembership(this ISiloHostBuilder builder,
            Action<ConsulMembershipOptions> configureOptions)
        {
            return builder.ConfigureServices(services => services.UseConsulMembership(configureOptions));
        }

        /// <summary>
        /// Configure siloHostBuilder to use ConsulMembership
        /// </summary>
        /// <param name="builder"></param>
        /// <param name="configuration"></param>
        /// <returns></returns>
        public static ISiloHostBuilder UseConsulMembership(this ISiloHostBuilder builder,
            IConfiguration configuration)
        {
            return builder.ConfigureServices(services => services.UseConsulMembership(configuration));
        }
    }
}
