using System;

using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
using Orleans.Runtime.ReminderService;

namespace Orleans.Hosting
{
    /// <summary>
    /// Silo host builder extensions.
    /// </summary>
    public static class SiloHostBuilderReminderExtensions
    {
        /// <summary>Adds reminder storage using ADO.NET.</summary>
        /// <param name="builder">The builder.</param>
        /// <param name="configureOptions">Configuration delegate.</param>
        /// <returns>The provided <see cref="ISiloHostBuilder"/>, for chaining.</returns>
        public static ISiloHostBuilder UseAdoNetReminderService(
            this ISiloHostBuilder builder,
            Action<AdoNetReminderTableOptions> configureOptions)
        {
            return builder.UseAdoNetReminderService(ob => ob.Configure(configureOptions));
        }

        /// <summary>Adds reminder storage using ADO.NET.</summary>
        /// <param name="builder">The builder.</param>
        /// <param name="configureOptions">Configuration delegate.</param>
        /// <returns>The provided <see cref="ISiloHostBuilder"/>, for chaining.</returns>
        public static ISiloHostBuilder UseAdoNetReminderService(
            this ISiloHostBuilder builder,
            Action<OptionsBuilder<AdoNetReminderTableOptions>> configureOptions)
        {
            return builder.ConfigureServices(services => services.UseAdoNetReminderService(configureOptions));
        }

        /// <summary>Adds reminder storage using ADO.NET.</summary>
        /// <param name="services">The service collection.</param>
        /// <param name="configureOptions">Configuration delegate.</param>
        /// <returns>The provided <see cref="ISiloHostBuilder"/>, for chaining.</returns>
        public static IServiceCollection UseAdoNetReminderService(this IServiceCollection services, Action<OptionsBuilder<AdoNetReminderTableOptions>> configureOptions)
        {
            services.AddSingleton<IReminderTable, AdoNetReminderTable>();
            services.ConfigureFormatter<AdoNetReminderTableOptions>();
            services.AddSingleton<IConfigurationValidator, AdoNetReminderTableOptionsValidator>();
            configureOptions(services.AddOptions<AdoNetReminderTableOptions>());
            return services;
        }
    }
}