using System;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
using Orleans.Transactions.AzureStorage;

namespace Orleans.Hosting
{
    public static class SiloBuilderExtensions
    {
        /// <summary>
        /// Configure cluster to use azure transaction log using configure action.
        /// </summary>
        public static ISiloHostBuilder UseAzureTransactionLog(this ISiloHostBuilder builder, Action<AzureTransactionLogOptions> configureOptions)
        {
            return builder.UseAzureTransactionLog(ob => ob.Configure(configureOptions));
        }

        /// <summary>
        /// Configure cluster to use azure transaction log using configuration builder.
        /// </summary>
        public static ISiloHostBuilder UseAzureTransactionLog(this ISiloHostBuilder builder, Action<OptionsBuilder<AzureTransactionLogOptions>> configureOptions)
        {
            return builder.ConfigureServices(services =>
            {
                configureOptions?.Invoke(services.AddOptions<AzureTransactionLogOptions>());
                services.AddTransient<IConfigurationValidator, AzureTransactionLogOptionsValidator>();
                services.AddTransient(AzureTransactionLogStorage.Create);
            });
        }
    }
}