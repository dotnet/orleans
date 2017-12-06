using System;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;
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
            return builder.ConfigureServices(services => services.UseAzureTransactionLog(configureOptions));
        }

        /// <summary>
        /// Configure cluster service to use azure transaction log using configure action.
        /// </summary>
        public static IServiceCollection UseAzureTransactionLog(this IServiceCollection services, Action<AzureTransactionLogOptions> configureOptions)
        {
            return services.UseAzureTransactionLog(ob => ob.Configure(configureOptions));
        }

        /// <summary>
        /// Configure cluster service to use azure transaction log using configuration builder.
        /// </summary>
        public static IServiceCollection UseAzureTransactionLog(this IServiceCollection services,
            Action<OptionsBuilder<AzureTransactionLogOptions>> configureOptions)
        {
            configureOptions?.Invoke(services.AddOptions<AzureTransactionLogOptions>());
            services.AddTransient<IConfigurationValidator,AzureTransactionLogOptionsValidator>();
            services.AddTransient(AzureTransactionLogStorage.Create);
            return services;
        }
    }
}
