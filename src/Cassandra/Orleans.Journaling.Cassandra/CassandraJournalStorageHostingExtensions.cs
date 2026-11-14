using Microsoft.Extensions.DependencyInjection;
using Orleans.Providers;
using Orleans.Runtime;

namespace Orleans.Journaling;

/// <summary>
/// Extension methods for configuring Cassandra journal storage.
/// </summary>
public static class CassandraJournalStorageHostingExtensions
{
    /// <summary>
    /// Configures Cassandra as the journal storage provider.
    /// </summary>
    /// <param name="builder">The silo builder.</param>
    /// <returns>The silo builder.</returns>
    public static ISiloBuilder AddCassandraJournalStorage(this ISiloBuilder builder)
        => builder.AddCassandraJournalStorage(configure: null);

    /// <summary>
    /// Configures Cassandra as the journal storage provider.
    /// </summary>
    /// <param name="builder">The silo builder.</param>
    /// <param name="configure">Configures the provider.</param>
    /// <returns>The silo builder.</returns>
    public static ISiloBuilder AddCassandraJournalStorage(
        this ISiloBuilder builder,
        Action<CassandraJournalStorageOptions>? configure)
        => builder.AddCassandraJournalStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, configure);

    /// <summary>
    /// Configures a named Cassandra journal storage provider.
    /// </summary>
    /// <param name="builder">The silo builder.</param>
    /// <param name="name">The provider name.</param>
    /// <param name="configure">Configures the provider.</param>
    /// <returns>The silo builder.</returns>
    public static ISiloBuilder AddCassandraJournalStorage(
        this ISiloBuilder builder,
        string name,
        Action<CassandraJournalStorageOptions>? configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var services = builder.Services;
        builder.AddJournalStorage(name, serviceProvider =>
            ActivatorUtilities.CreateInstance<CassandraJournalStorageProvider>(
                serviceProvider,
                serviceProvider.GetJournalStorageOptions<CassandraJournalStorageOptions>(name)));
        var options = services.AddJournalStorageOptions<CassandraJournalStorageOptions>(name);
        if (configure is not null)
        {
            options.Configure(configure);
        }

        return builder;
    }
}
