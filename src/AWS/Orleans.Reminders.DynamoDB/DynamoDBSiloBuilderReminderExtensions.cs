using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
using Orleans.Reminders.DynamoDB;
using System;

namespace Orleans.Hosting
{
    /// <summary>
    /// Silo host builder extensions.
    /// </summary>
    public static class DynamoDBSiloBuilderReminderExtensions
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
        /// The provided <see cref="ISiloBuilder"/>, for chaining.
        /// </returns>
        public static ISiloBuilder UseDynamoDBReminderService(this ISiloBuilder builder, Action<DynamoDBReminderStorageOptions> configure)
        {
            builder.ConfigureServices(services => services.UseDynamoDBReminderService(configure));
            return builder;
        }
    }
}