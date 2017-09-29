using System;
using Microsoft.Extensions.Configuration;
using Orleans.AzureUtils.Configuration;
using Orleans.Hosting;

namespace OrleansAzureUtils
{
    public static class ISiloHostBuilderExtensions
    {

        /// <summary>
        /// Configure DI to use AzureBasedMemebershipTable
        /// </summary>
        /// <param name="builder"></param>
        /// <param name="configureOptions"></param>
        public static ISiloHostBuilder UseAzureMemebershipTable(this ISiloHostBuilder builder,
            Action<AzureMembershipTableOptions> configureOptions)
        {
            return builder.ConfigureServices(services => services.UseAzureMemebershipTable(configureOptions));
        }

        /// <summary>
        /// Configure DI to use AzureBasedMemebershipTable
        /// </summary>
        /// <param name="builder"></param>
        /// <param name="config"></param>
        public static ISiloHostBuilder UseAzureMemebershipTable(this ISiloHostBuilder builder, IConfiguration config)
        {
            return builder.ConfigureServices(services => services.UseAzureMemebershipTable(config));
        }


        /// <summary>
        /// Configure DI to use AzureBasedMemebershipTable, and get its configuration from GlobalConfiguration.
        /// </summary>
        /// <param name="builder"></param>
        /// <returns></returns>
        public static ISiloHostBuilder UseAzureMembershipTableFromLegacyConfigurationSupport(
            this ISiloHostBuilder builder)
        {
            return builder.ConfigureServices(service =>
                service.UseAzureMembershipTableFromLegacyConfigurationSupport());
        }
    }
}
