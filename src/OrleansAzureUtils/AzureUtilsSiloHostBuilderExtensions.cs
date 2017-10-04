using System;
using Microsoft.Extensions.Configuration;
using Orleans.AzureUtils.Configuration;
using Orleans.Hosting;

namespace Microsoft.Orleans.Hosting
{
    public static class AzureUtilsSiloHostBuilderExtensions
    {

        /// <summary>
        /// Configure DI to use AzureTableBasedMemebership
        /// </summary>
        /// <param name="builder"></param>
        /// <param name="configureOptions"></param>
        public static ISiloHostBuilder UseAzureTableMemebership(this ISiloHostBuilder builder,
            Action<AzureTableMembershipOptions> configureOptions)
        {
            return builder.ConfigureServices(services => services.UseAzureTableMemebership(configureOptions));
        }

        /// <summary>
        /// Configure DI to use AzureTableBasedMemebership
        /// </summary>
        /// <param name="builder"></param>
        /// <param name="config"></param>
        public static ISiloHostBuilder UseAzureTableMemebership(this ISiloHostBuilder builder, IConfiguration config)
        {
            return builder.ConfigureServices(services => services.UseAzureTableMemebership(config));
        }
    }
}
