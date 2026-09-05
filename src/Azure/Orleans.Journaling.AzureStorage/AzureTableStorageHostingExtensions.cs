using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Configuration.Internal;

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
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.AddJournalStorage();

        var services = builder.Services;

        var options = builder.Services.AddOptions<AzureTableJournalStorageOptions>();
        if (configure is not null)
        {
            options.Configure(configure);
        }

        if (!services.Any(service => service.ServiceType.Equals(typeof(AzureTableJournalStorageProvider))))
        {
            builder.Services.TryAddSingleton<AzureTableJournalStorageInstruments>();
            builder.Services.AddSingleton<AzureTableJournalStorageProvider>();
            builder.Services.AddFromExisting<IJournalStorageProvider, AzureTableJournalStorageProvider>();
            builder.Services.AddFromExisting<IJournalStorageCatalog, AzureTableJournalStorageProvider>();
            builder.Services.AddFromExisting<ILifecycleParticipant<ISiloLifecycle>, AzureTableJournalStorageProvider>();
        }
        return builder;
    }
}
