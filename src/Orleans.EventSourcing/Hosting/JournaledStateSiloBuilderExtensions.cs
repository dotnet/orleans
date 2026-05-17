using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.EventSourcing;
using Orleans.EventSourcing.JournaledState;
using Orleans.Journaling;
using Orleans.Providers;
using Orleans.Runtime;

#nullable disable
#pragma warning disable ORLEANSEXP005
namespace Orleans.Hosting
{
    public static class JournaledStateSiloBuilderExtensions
    {
        /// <summary>
        /// Adds a journaled-state log consistency provider as the default consistency provider.
        /// </summary>
        public static ISiloBuilder AddJournaledStateBasedLogConsistencyProviderAsDefault(this ISiloBuilder builder)
        {
            ArgumentNullException.ThrowIfNull(builder);

            return builder.AddJournaledStateBasedLogConsistencyProvider(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME);
        }

        /// <summary>
        /// Adds a journaled-state log consistency provider.
        /// </summary>
        public static ISiloBuilder AddJournaledStateBasedLogConsistencyProvider(this ISiloBuilder builder, string name = "JournaledState")
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentException.ThrowIfNullOrWhiteSpace(name);

            builder.AddJournalStorage();
            return builder.ConfigureServices(services => services.AddJournaledStateBasedLogConsistencyProvider(name));
        }

        internal static IServiceCollection AddJournaledStateBasedLogConsistencyProvider(this IServiceCollection services, string name)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentException.ThrowIfNullOrWhiteSpace(name);

            services.AddLogConsistencyProtocolServicesFactory();
            services.TryAddSingleton<ILogViewAdaptorFactory>(sp => sp.GetKeyedService<ILogViewAdaptorFactory>(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME));
            return services.AddKeyedSingleton<ILogViewAdaptorFactory, LogConsistencyProvider>(name);
        }
    }
}
#pragma warning restore ORLEANSEXP005
