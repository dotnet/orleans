using Microsoft.Extensions.DependencyInjection;
using Orleans.Providers;

namespace Orleans.Journaling;

/// <summary>
/// Extension methods for configuring journal storage on embedded Garnet partitions.
/// </summary>
public static class GarnetJournalStorageHostingExtensions
{
    /// <summary>
    /// Configures Garnet as the journal storage provider.
    /// </summary>
    /// <param name="builder">The silo builder.</param>
    /// <param name="configure">The Garnet journal storage configuration delegate.</param>
    /// <returns>The silo builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
    public static ISiloBuilder AddGarnetJournalStorage(this ISiloBuilder builder, Action<GarnetJournalStorageOptions>? configure)
        => builder.AddGarnetJournalStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, configure);

    /// <summary>
    /// Configures a named Garnet journal storage provider.
    /// </summary>
    /// <param name="builder">The silo builder.</param>
    /// <param name="name">The provider name.</param>
    /// <param name="configure">The delegate used to configure this provider, or <see langword="null"/> to use separately configured options.</param>
    /// <returns>The silo builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty or whitespace.</exception>
    public static ISiloBuilder AddGarnetJournalStorage(
        this ISiloBuilder builder,
        string name,
        Action<GarnetJournalStorageOptions>? configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var services = builder.Services;
        builder.AddJournalStorage(name, serviceProvider =>
            ActivatorUtilities.CreateInstance<GarnetJournalStorageProvider>(
                serviceProvider,
                serviceProvider.GetJournalStorageOptions<GarnetJournalStorageOptions>(name)));
        var options = services.AddJournalStorageOptions<GarnetJournalStorageOptions>(name);
        if (configure is not null)
        {
            options.Configure(configure);
        }

        if (!services.Any(service => service.IsKeyedService
            && service.ServiceType == typeof(GarnetJournalStorageOptionsValidator)
            && Equals(service.ServiceKey, name)))
        {
            services.AddKeyedSingleton<GarnetJournalStorageOptionsValidator>(name, (serviceProvider, _) =>
                new GarnetJournalStorageOptionsValidator(
                    serviceProvider.GetJournalStorageOptions<GarnetJournalStorageOptions>(name).Value, name));
            services.AddTransient<IConfigurationValidator>(serviceProvider =>
                serviceProvider.GetRequiredKeyedService<GarnetJournalStorageOptionsValidator>(name));
        }

        return builder;
    }
}
