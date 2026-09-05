using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration.Internal;
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
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.AddJournalStorage();

        var services = builder.Services;
        var options = services.AddOptions<RedisJournalStorageOptions>();
        if (configure is not null)
        {
            options.Configure(configure);
        }

        services.AddTransient<IConfigurationValidator>(
            serviceProvider => new RedisJournalStorageOptionsValidator(serviceProvider.GetRequiredService<IOptions<RedisJournalStorageOptions>>().Value));

        if (!services.Any(service => service.ServiceType.Equals(typeof(RedisJournalStorageProvider))))
        {
            services.AddSingleton<RedisJournalStorageProvider>();
            services.AddFromExisting<IJournalStorageProvider, RedisJournalStorageProvider>();
            services.AddFromExisting<IJournalStorageCatalog, RedisJournalStorageProvider>();
            services.AddFromExisting<ILifecycleParticipant<ISiloLifecycle>, RedisJournalStorageProvider>();
        }

        return builder;
    }
}
