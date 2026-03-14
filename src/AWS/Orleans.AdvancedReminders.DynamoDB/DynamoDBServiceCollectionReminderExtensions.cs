using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
using Orleans.AdvancedReminders.DynamoDB;
using System;

namespace Orleans.Hosting
{
    /// <summary>
    /// <see cref="IServiceCollection"/> extensions.
    /// </summary>
    public static class DynamoDBServiceCollectionReminderExtensions
    {
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
        public static IServiceCollection UseDynamoDBAdvancedReminderService(this IServiceCollection services, Action<DynamoDBReminderStorageOptions> configure)
        {
            services.AddAdvancedReminders();
            services.AddSingleton<Orleans.AdvancedReminders.IReminderTable, DynamoDBReminderTable>();
            services.Configure<DynamoDBReminderStorageOptions>(configure);
            services.ConfigureFormatter<DynamoDBReminderStorageOptions>();
            return services;
        }
    }
}
