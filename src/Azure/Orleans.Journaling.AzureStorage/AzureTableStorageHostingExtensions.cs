using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Providers;

namespace Orleans.Journaling;

/// <summary>
/// Extensions for configuring Azure Table Storage as the journal storage provider.
/// </summary>
public static class AzureTableStorageHostingExtensions
{
    /// <summary>
    /// Configures Azure Table Storage as the journal storage provider.
    /// </summary>
    /// <param name="builder">The silo builder.</param>
    /// <returns>The silo builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
    public static ISiloBuilder AddAzureTableJournalStorage(this ISiloBuilder builder) => builder.AddAzureTableJournalStorage(configure: null);

    /// <summary>
    /// Configures Azure Table Storage as the journal storage provider.
    /// </summary>
    /// <param name="builder">The silo builder.</param>
    /// <param name="configure">The delegate used to configure the journal storage provider.</param>
    /// <returns>The silo builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
    public static ISiloBuilder AddAzureTableJournalStorage(this ISiloBuilder builder, Action<AzureTableJournalStorageOptions>? configure)
        => builder.AddAzureTableJournalStorage(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME, configure);

    /// <summary>
    /// Configures a named Azure Table Storage journal provider.
    /// </summary>
    /// <param name="builder">The silo builder.</param>
    /// <param name="name">The provider name.</param>
    /// <param name="configure">The delegate used to configure this provider.</param>
    /// <returns>The silo builder.</returns>
    /// <remarks>
    /// Named providers share journal format configuration but have independent storage options and lifecycle initialization.
    /// The default provider uses unnamed options; other providers use options named after the provider.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty or whitespace.</exception>
    public static ISiloBuilder AddAzureTableJournalStorage(
        this ISiloBuilder builder,
        string name,
        Action<AzureTableJournalStorageOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var services = builder.Services;
        services.TryAddSingleton<AzureTableJournalStorageInstruments>();
        builder.AddJournalStorage(name, serviceProvider =>
            ActivatorUtilities.CreateInstance<AzureTableJournalStorageProvider>(
                serviceProvider,
                serviceProvider.GetJournalStorageOptions<AzureTableJournalStorageOptions>(name)));
        var options = services.AddJournalStorageOptions<AzureTableJournalStorageOptions>(name);
        if (configure is not null)
        {
            options.Configure(configure);
        }

        return builder;
    }
}
