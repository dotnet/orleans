using System;
using Microsoft.Extensions.Options;
using Orleans.DurableReminders.AzureStorage;

namespace Orleans.Hosting
{
    /// <summary>
    /// Silo host builder extensions.
    /// </summary>
    public static class AzureStorageReminderSiloBuilderExtensions
    {
        /// <summary>
        /// Adds reminder storage backed by Azure Table Storage.
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
        public static ISiloBuilder UseAzureTableDurableReminderService(this ISiloBuilder builder, Action<AzureTableReminderStorageOptions> configure)
        {
            builder.ConfigureServices(services => services.UseAzureTableDurableReminderService(configure));
            return builder;
        }

        /// <summary>
        /// Adds reminder storage backed by Azure Table Storage.
        /// </summary>
        /// <param name="builder">
        /// The builder.
        /// </param>
        /// <param name="configureOptions">
        /// The configuration delegate.
        /// </param>
        /// <returns>
        /// The provided <see cref="ISiloBuilder"/>, for chaining.
        /// </returns>
        public static ISiloBuilder UseAzureTableDurableReminderService(this ISiloBuilder builder, Action<OptionsBuilder<AzureTableReminderStorageOptions>> configureOptions)
        {
            builder.ConfigureServices(services => services.UseAzureTableDurableReminderService(configureOptions));
            return builder;
        }

        /// <summary>
        /// Adds reminder storage backed by Azure Table Storage.
        /// </summary>
        /// <param name="builder">
        /// The builder.
        /// </param>
        /// <param name="connectionString">
        /// The storage connection string.
        /// </param>
        /// <returns>
        /// The provided <see cref="ISiloBuilder"/>, for chaining.
        /// </returns>
        public static ISiloBuilder UseAzureTableDurableReminderService(this ISiloBuilder builder, string connectionString)
        {
            builder.UseAzureTableDurableReminderService(options =>
            {
                if (Uri.TryCreate(connectionString, UriKind.Absolute, out var uri))
                {
                    options.TableServiceClient = new(uri);
                }
                else
                {
                    options.TableServiceClient = new(connectionString);
                }
            });
            return builder;
        }
    }
}
