using System;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;

namespace Orleans.Transactions.Azure
{
    public static class SiloBuilderExtensions
    {
        /// <summary>
        /// Configure cluster to use azure transaction log.
        /// </summary>
        public static ISiloHostBuilder UseAzureTransactionLog(this ISiloHostBuilder builder, Action<AzureTransactionLogOptions> configureOptions)
        {
            return builder.ConfigureServices(services => services.UseAzureTransactionLog(configureOptions));
        }

        /// <summary>
        /// Configure cluster to use azure transaction log.
        /// </summary>
        public static IServiceCollection UseAzureTransactionLog(this IServiceCollection services, Action<AzureTransactionLogOptions> configureOptions)
        {
            return services.UseAzureTransactionLog(ob => ob.Configure(configureOptions));
        }

        /// <summary>
        /// Configure cluster to use azure transaction log.
        /// </summary>
        public static IServiceCollection UseAzureTransactionLog(this IServiceCollection services,
            Action<OptionsBuilder<AzureTransactionLogOptions>> configureOptions)
        {
            configureOptions?.Invoke(services.AddOptions<AzureTransactionLogOptions>());
            return services.AddTransient(AzureTransactionLogStorage.Create);
        }
    }
}
