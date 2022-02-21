using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration.Internal;
using Orleans.Runtime;
using Orleans.Runtime.ReminderServiceV2;

namespace Orleans.Hosting
{
    /// <summary>
    /// Extensions to <see cref="ISiloBuilder"/> for configuring reminder storage.
    /// </summary>
    public static class SiloBuilderReminderV2Extensions
    {
        /// <summary>
        /// Configures reminder storage using an in-memory, non-persistent store.
        /// </summary>
        /// <remarks>
        /// Note that this is for development and testing scenarios only and should not be used in production.
        /// </remarks>
        /// <param name="builder">The silo host builder.</param>
        /// <returns>The provided <see cref="ISiloBuilder"/>, for chaining.</returns>
        public static ISiloBuilder UseInMemoryReminderV2Service(this ISiloBuilder builder)
        {
            // The reminder table is a reference to a singleton IReminderTableGrain.
            builder.ConfigureServices(services => services.UseInMemoryReminderV2Service());
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
        internal static IServiceCollection UseInMemoryReminderV2Service(this IServiceCollection services)
        {
            services.AddSingleton<InMemoryReminderV2Table>();
            services.AddFromExisting<IReminderTable, InMemoryReminderV2Table>();
            services.AddFromExisting<ILifecycleParticipant<ISiloLifecycle>, InMemoryReminderV2Table>();
            return services;
        }
    }
}