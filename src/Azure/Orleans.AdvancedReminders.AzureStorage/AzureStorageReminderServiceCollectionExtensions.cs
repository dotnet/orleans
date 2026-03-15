using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Configuration.Internal;
using Orleans.AdvancedReminders.AzureStorage;
namespace Orleans.Hosting
{
    /// <summary>
    /// <see cref="IServiceCollection"/> extensions.
    /// </summary>
    public static class AzureStorageReminderServiceCollectionExtensions
    {
        /// <summary>
        /// Adds reminder storage backed by Azure Table Storage.
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
        public static IServiceCollection UseAzureTableAdvancedReminderService(this IServiceCollection services, Action<AzureTableReminderStorageOptions> configure)
        {
            services.AddAdvancedReminders();
            services.UseAzureBlobDurableJobs(options =>
            {
                options.Configure<IOptions<AzureTableReminderStorageOptions>>((jobOptions, storageOptions) =>
                {
                    jobOptions.BlobServiceClient = storageOptions.Value.BlobServiceClient;
                    jobOptions.ContainerName = storageOptions.Value.JobContainerName;
                });
            });
            services.AddSingleton<Orleans.AdvancedReminders.IReminderTable, AzureBasedReminderTable>();
            services.Configure<AzureTableReminderStorageOptions>(configure);
            services.ConfigureFormatter<AzureTableReminderStorageOptions>();
            return services;
        }

        /// <summary>
        /// Adds reminder storage backed by Azure Table Storage.
        /// </summary>
        /// <param name="services">
        /// The service collection.
        /// </param>
        /// <param name="configureOptions">
        /// The configuration delegate.
        /// </param>
        /// <returns>
        /// The provided <see cref="IServiceCollection"/>, for chaining.
        /// </returns>
        public static IServiceCollection UseAzureTableAdvancedReminderService(this IServiceCollection services, Action<OptionsBuilder<AzureTableReminderStorageOptions>> configureOptions)
        {
            services.AddAdvancedReminders();
            services.UseAzureBlobDurableJobs(options =>
            {
                options.Configure<IOptions<AzureTableReminderStorageOptions>>((jobOptions, storageOptions) =>
                {
                    jobOptions.BlobServiceClient = storageOptions.Value.BlobServiceClient;
                    jobOptions.ContainerName = storageOptions.Value.JobContainerName;
                });
            });
            services.AddSingleton<Orleans.AdvancedReminders.IReminderTable, AzureBasedReminderTable>();
            configureOptions?.Invoke(services.AddOptions<AzureTableReminderStorageOptions>());
            services.ConfigureFormatter<AzureTableReminderStorageOptions>();
            services.AddTransient<IConfigurationValidator>(sp => new AzureTableReminderStorageOptionsValidator(sp.GetRequiredService<IOptionsMonitor<AzureTableReminderStorageOptions>>().Get(Options.DefaultName), Options.DefaultName));
            return services;
        }

        /// <summary>
        /// Adds reminder storage backed by Azure Table Storage.
        /// </summary>
        /// <param name="services">
        /// The service collection.
        /// </param>
        /// <param name="connectionString">
        /// The storage connection string.
        /// </param>
        /// <returns>
        /// The provided <see cref="IServiceCollection"/>, for chaining.
        /// </returns>
        public static IServiceCollection UseAzureTableAdvancedReminderService(this IServiceCollection services, string connectionString)
        {
            services.UseAzureTableAdvancedReminderService(options =>
            {
                if (Uri.TryCreate(connectionString, UriKind.Absolute, out var uri))
                {
                    options.TableServiceClient = new(uri);
                    options.BlobServiceClient = new(CreateBlobServiceUri(uri));
                }
                else
                {
                    options.TableServiceClient = new(connectionString);
                    options.BlobServiceClient = new(connectionString);
                }
            });
            return services;
        }

        private static Uri CreateBlobServiceUri(Uri serviceUri)
        {
            if (serviceUri.Host.Contains(".table.", StringComparison.OrdinalIgnoreCase))
            {
                var builder = new UriBuilder(serviceUri)
                {
                    Host = serviceUri.Host.Replace(".table.", ".blob.", StringComparison.OrdinalIgnoreCase),
                };

                return builder.Uri;
            }

            return serviceUri;
        }
    }
}
