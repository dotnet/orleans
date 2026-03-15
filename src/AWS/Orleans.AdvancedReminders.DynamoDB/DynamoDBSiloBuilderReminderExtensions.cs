using System;
using Orleans.AdvancedReminders.DynamoDB;

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
        public static ISiloBuilder UseDynamoDBAdvancedReminderService(this ISiloBuilder builder, Action<DynamoDBReminderStorageOptions> configure)
        {
            builder.ConfigureServices(services => services.UseDynamoDBAdvancedReminderService(configure));
            return builder;
        }
    }
}
