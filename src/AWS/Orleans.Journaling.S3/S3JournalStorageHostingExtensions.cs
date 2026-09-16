using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Providers;

namespace Orleans.Journaling;

/// <summary>
/// Extension methods for configuring Amazon S3 journal storage.
/// </summary>
public static class S3JournalStorageHostingExtensions
{
    /// <summary>
    /// Configures Amazon S3 as the journal storage provider.
    /// </summary>
    /// <param name="builder">The silo builder.</param>
    /// <returns>The silo builder.</returns>
    public static ISiloBuilder AddS3JournalStorage(this ISiloBuilder builder) => builder.AddS3JournalStorage(configure: null);

    /// <summary>
    /// Configures Amazon S3 as the journal storage provider.
    /// </summary>
    /// <param name="builder">The silo builder.</param>
    /// <param name="configure">The Amazon S3 journal storage configuration delegate.</param>
    /// <returns>The silo builder.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="builder"/> is <see langword="null"/>.
    /// </exception>
    public static ISiloBuilder AddS3JournalStorage(this ISiloBuilder builder, Action<S3JournalStorageOptions>? configure)
        => builder.AddS3JournalStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, configure);

    /// <summary>
    /// Configures a named Amazon S3 journal provider.
    /// </summary>
    /// <param name="builder">The silo builder.</param>
    /// <param name="name">The provider name.</param>
    /// <param name="configure">The delegate used to configure this provider, or <see langword="null"/> to use separately configured options.</param>
    /// <returns>The silo builder.</returns>
    /// <remarks>
    /// Named providers share journal format configuration but have independent storage options and client lifetimes.
    /// The default provider uses unnamed options; other providers use options named after the provider.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty or whitespace.</exception>
    public static ISiloBuilder AddS3JournalStorage(
        this ISiloBuilder builder,
        string name,
        Action<S3JournalStorageOptions>? configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var services = builder.Services;
        services.TryAddSingleton<S3JournalStorageInstruments>();
        builder.AddJournalStorage(name, serviceProvider =>
            ActivatorUtilities.CreateInstance<S3JournalStorageProvider>(
                serviceProvider,
                serviceProvider.GetJournalStorageOptions<S3JournalStorageOptions>(name)));
        var options = services.AddJournalStorageOptions<S3JournalStorageOptions>(name);
        if (configure is not null)
        {
            options.Configure(configure);
        }

        return builder;
    }
}
