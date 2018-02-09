using System;

using Microsoft.Extensions.DependencyInjection;

using OrleansAWSUtils.Reminders;

namespace Orleans.Hosting
{
    /// <summary>
    /// Silo host builder extensions.
    /// </summary>
    public static class SiloHostBuilderReminderExtensions
    {
        /// <summary>
        /// Adds reminder storage backed by Amazon DynamoDB.
        /// </summary>
        /// <param name="builder">
        /// The builder.
        /// </param>
        /// <param name="configure">
        /// The delegate used to configure the reminder store.
        /// </param>
        /// <returns>
        /// The provided <see cref="ISiloHostBuilder"/>, for chaining.
        /// </returns>
        public static ISiloHostBuilder UseDynamoDBReminderService(this ISiloHostBuilder builder, Action<DynamoDBReminderTableOptions> configure)
        {
            builder.ConfigureServices(services => services.UseDynamoDBReminderService(configure));
            return builder;
        }

        /// <summary>
        /// Adds reminder storage backed by Amazon DynamoDB.
        /// </summary>
        /// <param name="builder">
        /// The builder.
        /// </param>
        /// <param name="connectionString">
        /// The storage connection string.
        /// </param>
        /// <returns>
        /// The provided <see cref="ISiloHostBuilder"/>, for chaining.
        /// </returns>
        public static ISiloHostBuilder UseDynamoDBReminderService(this ISiloHostBuilder builder, string connectionString)
        {
            builder.UseDynamoDBReminderService(options => options.ConnectionString = connectionString);
            return builder;
        }

        /// <summary>
        /// Adds reminder storage backed by Amazon DynamoDB.
        /// </summary>
        /// <param name="services">
        /// The service collection.
        /// </param>
        /// <param name="configure">
        /// The delegate used to configure the reminder store.
        /// </param>
        /// <returns>
        /// The provided <see cref="IServiceCollection"/>, for chaining.
        /// </returns>
        public static IServiceCollection UseDynamoDBReminderService(this IServiceCollection services, Action<DynamoDBReminderTableOptions> configure)
        {
            services.AddSingleton<IReminderTable, DynamoDBReminderTable>();
            services.Configure(configure);
            services.TryConfigureFormatter<DynamoDBReminderTableOptions, DynamoDBReminderTableOptionsFormatter>();
            return services;
        }

        /// <summary>
        /// Adds reminder storage backed by Amazon DynamoDB.
        /// </summary>
        /// <param name="services">
        /// The service collection.
        /// </param>
        /// <param name="configure">
        /// The delegate used to configure the reminder store.
        /// </param>
        /// <returns>
        /// The provided <see cref="IServiceCollection"/>, for chaining.
        /// </returns>
        public static IServiceCollection UseDynamoDBReminderService(this IServiceCollection services, string connectionString)
        {
            services.UseDynamoDBReminderService(options => options.ConnectionString = connectionString);
            return services;
        }
    }
}