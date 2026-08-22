using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Configuration.Internal;
using Orleans.Runtime;

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
    public static ISiloBuilder AddS3JournalStorage(this ISiloBuilder builder, Action<S3JournalStorageOptions>? configure)
    {
        builder.AddJournalStorage();

        var services = builder.Services;
        var options = services.AddOptions<S3JournalStorageOptions>();
        if (configure is not null)
        {
            options.Configure(configure);
        }

        if (!services.Any(service => service.ServiceType.Equals(typeof(S3JournalStorageProvider))))
        {
            services.TryAddSingleton<S3JournalStorageInstruments>();
            services.AddSingleton<S3JournalStorageProvider>();
            services.AddFromExisting<IJournalStorageProvider, S3JournalStorageProvider>();
            services.AddFromExisting<IJournalStorageCatalog, S3JournalStorageProvider>();
            services.AddFromExisting<ILifecycleParticipant<ISiloLifecycle>, S3JournalStorageProvider>();
        }

        return builder;
    }
}
