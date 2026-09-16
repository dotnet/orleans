using Microsoft.Extensions.DependencyInjection;
using Orleans.Providers;
using Orleans.Runtime;

namespace Orleans.Journaling;

/// <summary>
/// Extension methods for configuring Redis journal storage.
/// </summary>
public static class RedisJournalStorageHostingExtensions
{
    /// <summary>
    /// Configures Redis as the journal storage provider.
    /// </summary>
    /// <param name="builder">The silo builder.</param>
    /// <returns>The silo builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
    public static ISiloBuilder AddRedisJournalStorage(this ISiloBuilder builder) => builder.AddRedisJournalStorage(configure: null);

    /// <summary>
    /// Configures Redis as the journal storage provider.
    /// </summary>
    /// <param name="builder">The silo builder.</param>
    /// <param name="configure">The Redis journal storage configuration delegate.</param>
    /// <returns>The silo builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
    public static ISiloBuilder AddRedisJournalStorage(this ISiloBuilder builder, Action<RedisJournalStorageOptions>? configure)
        => builder.AddRedisJournalStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, configure);

    /// <summary>
    /// Configures a named Redis journal provider.
    /// </summary>
    /// <param name="builder">The silo builder.</param>
    /// <param name="name">The provider name.</param>
    /// <param name="configure">The delegate used to configure this provider, or <see langword="null"/> to use separately configured options.</param>
    /// <returns>The silo builder.</returns>
    /// <remarks>
    /// Named providers share journal format configuration but have independent options, validation, and connection lifetimes.
    /// The default provider uses unnamed options; other providers use options named after the provider.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty or whitespace.</exception>
    public static ISiloBuilder AddRedisJournalStorage(
        this ISiloBuilder builder,
        string name,
        Action<RedisJournalStorageOptions>? configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var services = builder.Services;
        builder.AddJournalStorage(name, serviceProvider =>
            ActivatorUtilities.CreateInstance<RedisJournalStorageProvider>(
                serviceProvider,
                serviceProvider.GetJournalStorageOptions<RedisJournalStorageOptions>(name)));
        var options = services.AddJournalStorageOptions<RedisJournalStorageOptions>(name);
        if (configure is not null)
        {
            options.Configure(configure);
        }

        if (!services.Any(service => service.IsKeyedService
            && service.ServiceType == typeof(RedisJournalStorageOptionsValidator)
            && Equals(service.ServiceKey, name)))
        {
            services.AddKeyedSingleton<RedisJournalStorageOptionsValidator>(name, (serviceProvider, _) =>
                new RedisJournalStorageOptionsValidator(
                    serviceProvider.GetJournalStorageOptions<RedisJournalStorageOptions>(name).Value, name));
            services.AddTransient<IConfigurationValidator>(serviceProvider =>
                serviceProvider.GetRequiredKeyedService<RedisJournalStorageOptionsValidator>(name));
        }

        return builder;
    }
}
