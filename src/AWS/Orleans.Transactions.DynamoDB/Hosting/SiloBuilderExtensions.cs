using System;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
using Orleans.Transactions.DynamoDB;

namespace Orleans.Hosting
{
    public static class SiloBuilderExtensions
    {
        /// <summary>
        /// Configure cluster to use dynamoDB transaction log using configure action.
        /// </summary>
        public static ISiloHostBuilder UseDynamoDBTransactionLog(this ISiloHostBuilder builder, Action<DynamoDBTransactionLogOptions> configureOptions)
        {
            return builder.UseDynamoDBTransactionLog(ob => ob.Configure(configureOptions));
        }

        /// <summary>
        /// Configure cluster to use dynamoDB transaction log using configuration builder.
        /// </summary>
        public static ISiloHostBuilder UseDynamoDBTransactionLog(this ISiloHostBuilder builder, Action<OptionsBuilder<DynamoDBTransactionLogOptions>> configureOptions)
        {
            return builder.ConfigureServices(services => services.UseDynamoDBTransactionLog(configureOptions));
        }

        /// <summary>
        /// Configure cluster service to use dynamoDB transaction log using configure action.
        /// </summary>
        public static IServiceCollection UseDynamoDBTransactionLog(this IServiceCollection services, Action<DynamoDBTransactionLogOptions> configureOptions)
        {
            return services.UseDynamoDBTransactionLog(ob => ob.Configure(configureOptions));
        }

        /// <summary>
        /// Configure cluster service to use dynamoDB transaction log using configuration builder.
        /// </summary>
        public static IServiceCollection UseDynamoDBTransactionLog(this IServiceCollection services,
            Action<OptionsBuilder<DynamoDBTransactionLogOptions>> configureOptions)
        {
            configureOptions?.Invoke(services.AddOptions<DynamoDBTransactionLogOptions>());
            services.AddTransient<IConfigurationValidator, DynamoDBTransactionLogOptionsValidator>();
            services.AddTransient(DynamoDBTransactionLogStorage.Create);
            return services;
        }
    }
}
