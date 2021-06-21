using System;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime.ReminderService;

namespace Orleans.Hosting
{
    /// <summary>
    /// Silo host builder extensions.
    /// </summary>
    public static class SiloBuilderReminderExtensions
    {
        /// <summary>Adds reminder storage using ADO.NET. Instructions on configuring your database are available at <see href="http://aka.ms/orleans-sql-scripts"/>.</summary>
        /// <param name="builder">The builder.</param>
        /// <param name="configureOptions">Configuration delegate.</param>
        /// <returns>The provided <see cref="ISiloBuilder"/>, for chaining.</returns>
        /// <remarks>
        /// Instructions on configuring your database are available at <see href="http://aka.ms/orleans-sql-scripts"/>.
        /// </remarks>
        public static ISiloBuilder UseAdoNetReminderService(
            this ISiloBuilder builder,
            Action<AdoNetReminderTableOptions> configureOptions)
        {
            return builder.UseAdoNetReminderService(ob => ob.Configure(configureOptions));
        }

        /// <summary>Adds reminder storage using ADO.NET. Instructions on configuring your database are available at <see href="http://aka.ms/orleans-sql-scripts"/>.</summary>
        /// <param name="builder">The builder.</param>
        /// <param name="configureOptions">Configuration delegate.</param>
        /// <returns>The provided <see cref="ISiloBuilder"/>, for chaining.</returns>
        /// <remarks>
        /// Instructions on configuring your database are available at <see href="http://aka.ms/orleans-sql-scripts"/>.
        /// </remarks>
        public static ISiloBuilder UseAdoNetReminderService(
            this ISiloBuilder builder,
            Action<OptionsBuilder<AdoNetReminderTableOptions>> configureOptions)
        {
            return builder.ConfigureServices(services => services.UseAdoNetReminderService(configureOptions));
        }

        /// <summary>Adds reminder storage using ADO.NET. Instructions on configuring your database are available at <see href="http://aka.ms/orleans-sql-scripts"/>.</summary>
        /// <param name="services">The service collection.</param>
        /// <param name="configureOptions">Configuration delegate.</param>
        /// <returns>The provided <see cref="IServiceCollection"/>, for chaining.</returns>
        /// <remarks>
        /// Instructions on configuring your database are available at <see href="http://aka.ms/orleans-sql-scripts"/>.
        /// </remarks>
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