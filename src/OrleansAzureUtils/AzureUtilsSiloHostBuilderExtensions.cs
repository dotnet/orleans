using System;
using Microsoft.Extensions.Configuration;
using Orleans.AzureUtils.Configuration;
using Orleans.Hosting;

namespace Orleans.Runtime.Hosting
{
    public static class AzureUtilsSiloHostBuilderExtensions
    {

        /// <summary>
        /// Configure DI to use AzureTableBasedMembership
        /// </summary>
        /// <param name="builder"></param>
        /// <param name="configureOptions"></param>
        public static ISiloHostBuilder UseAzureTableMembership(this ISiloHostBuilder builder,
            Action<AzureTableMembershipOptions> configureOptions)
        {
            return builder.ConfigureServices(services => services.UseAzureTableMembership(configureOptions));
        }

        /// <summary>
        /// Configure DI to use AzureTableBasedMembership
        /// </summary>
        /// <param name="builder"></param>
        /// <param name="config"></param>
        public static ISiloHostBuilder UseAzureTableMembership(this ISiloHostBuilder builder, IConfiguration config)
        {
            return builder.ConfigureServices(services => services.UseAzureTableMembership(config));
        }
    }
}
