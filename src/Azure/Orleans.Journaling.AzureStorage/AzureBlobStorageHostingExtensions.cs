using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Configuration.Internal;

namespace Orleans.Journaling;

/// <summary>
/// Extensions for configuring Azure Blob Storage as the journal storage provider.
/// </summary>
public static class AzureBlobStorageHostingExtensions
{
    /// <summary>
    /// Configures Azure Blob Storage as the journal storage provider.
    /// </summary>
    /// <param name="builder">The silo builder.</param>
    /// <returns>The silo builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
    public static ISiloBuilder AddAzureBlobJournalStorage(this ISiloBuilder builder) => builder.AddAzureBlobJournalStorage(configure: null);

    /// <summary>
    /// Configures Azure Blob Storage as the journal storage provider.
    /// </summary>
    /// <param name="builder">The silo builder.</param>
    /// <param name="configure">The delegate used to configure the journal storage provider.</param>
    /// <returns>The silo builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
    public static ISiloBuilder AddAzureBlobJournalStorage(this ISiloBuilder builder, Action<AzureBlobJournalStorageOptions>? configure)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.AddJournalStorage();

        var services = builder.Services;

        var options = builder.Services.AddOptions<AzureBlobJournalStorageOptions>();
        if (configure is not null)
        {
            options.Configure(configure);
        }

        if (!services.Any(service => service.ServiceType.Equals(typeof(AzureBlobJournalStorageProvider))))
        {
            builder.Services.TryAddSingleton<AzureBlobJournalStorageInstruments>();
            builder.Services.AddSingleton<AzureBlobJournalStorageProvider>();
            builder.Services.AddFromExisting<IJournalStorageProvider, AzureBlobJournalStorageProvider>();
            builder.Services.AddFromExisting<IJournalStorageCatalog, AzureBlobJournalStorageProvider>();
            builder.Services.AddFromExisting<ILifecycleParticipant<ISiloLifecycle>, AzureBlobJournalStorageProvider>();
        }
        return builder;
    }
}
