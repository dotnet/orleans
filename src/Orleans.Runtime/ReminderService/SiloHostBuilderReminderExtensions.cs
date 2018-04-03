using Microsoft.Extensions.DependencyInjection;

using Orleans.Runtime;

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
        /// <param name="services">The service collection.</param>
        /// <returns>The provided <see cref="IServiceCollection"/>, for chaining.</returns>
        internal static IServiceCollection UseInMemoryReminderService(this IServiceCollection services)
        {
            // The reminder table is a reference to a singleton IReminderTableGrain.
            services.AddSingleton<IReminderTable>(sp => sp.GetRequiredService<IGrainFactory>().GetGrain<IReminderTableGrain>(Constants.ReminderTableGrainId));
            return services;
        }
    }
}