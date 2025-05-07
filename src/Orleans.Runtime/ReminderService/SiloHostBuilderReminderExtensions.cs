using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration;
using Orleans.Configuration.Internal;
using Orleans.Runtime;
using Orleans.Runtime.ReminderService;
using Orleans.Timers;

namespace Orleans.Hosting
{
    /// <summary>
    /// Extensions to <see cref="ISiloHostBuilder"/> for configuring reminder storage.
    /// </summary>
    public static class SiloHostBuilderReminderExtensions
    {
        /// <summary>
        /// Configures reminder storage using an in-memory, non-persistent store.
        /// </summary>
        /// <remarks>
        /// Note that this is for development and testing scenarios only and should not be used in production.
        /// </remarks>
        /// <param name="builder">The silo host builder.</param>
        /// <returns>The provided <see cref="ISiloHostBuilder"/>, for chaining.</returns>
        public static ISiloHostBuilder UseInMemoryReminderService(this ISiloHostBuilder builder)
        {
            // The reminder table is a reference to a singleton IReminderTableGrain.
            builder.ConfigureServices(services => services.UseInMemoryReminderService());
            return builder;
        }

        /// <summary>
        /// Configures reminder storage using an in-memory, non-persistent store.
        /// </summary>
        /// <remarks>
        /// Note that this is for development and testing scenarios only and should not be used in production.
        /// </remarks>
        /// <param name="builder">The silo host builder.</param>
        /// <returns>The provided <see cref="ISiloBuilder"/>, for chaining.</returns>
        public static ISiloBuilder UseInMemoryReminderService(this ISiloBuilder builder)
        {
            // The reminder table is a reference to a singleton IReminderTableGrain.
            builder.ConfigureServices(services => services.UseInMemoryReminderService());
            return builder;
        }

        /// <summary>
        /// Configures reminder storage using an in-memory, non-persistent store.
        /// </summary>
        /// <remarks>
        /// Note that this is for development and testing scenarios only and should not be used in production.
        /// </remarks>
        /// <param name="services">The service collection.</param>
        /// <returns>The provided <see cref="IServiceCollection"/>, for chaining.</returns>
        internal static IServiceCollection UseInMemoryReminderService(this IServiceCollection services)
        {
            services.AddSingleton<InMemoryReminderTable>();
            services.AddFromExisting<IReminderTable, InMemoryReminderTable>();
            services.AddFromExisting<ILifecycleParticipant<ISiloLifecycle>, InMemoryReminderTable>();
            return services;
        }

        /// <summary>
        /// Adds support for reminders to this silo.
        /// </summary>
        /// <param name="builder">The builder.</param>
        /// <returns>The silo builder.</returns>
        public static ISiloBuilder AddReminders(this ISiloBuilder builder) => builder.ConfigureServices(AddReminders);

        /// <summary>
        /// Add support for reminders to this client.
        /// </summary>
        /// <param name="services">The services.</param>
        public static void AddReminders(this IServiceCollection services)
        {
            if (services.Any(service => service.ServiceType.Equals(typeof(LocalReminderService))))
            {
                return;
            }

            services.AddSingleton<LocalReminderService>();
            services.AddFromExisting<IReminderService, LocalReminderService>();
            // TODO?
            // services.AddFromExisting<ILifecycleParticipant<ISiloLifecycle>, LocalReminderService>();
            services.AddSingleton<IReminderRegistry, ReminderRegistry>();
        }
    }
}