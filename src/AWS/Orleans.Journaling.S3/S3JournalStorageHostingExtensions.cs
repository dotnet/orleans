using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration.Internal;
using Orleans.Runtime;

namespace Orleans.Journaling;

public static class S3JournalStorageHostingExtensions
{
    public static ISiloBuilder AddS3JournalStorage(this ISiloBuilder builder) => builder.AddS3JournalStorage(configure: null);

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
            services.AddSingleton<S3JournalStorageProvider>();
            services.AddFromExisting<IJournalStorageProvider, S3JournalStorageProvider>();
            services.AddFromExisting<IJournalStorageCatalog, S3JournalStorageProvider>();
            services.AddFromExisting<ILifecycleParticipant<ISiloLifecycle>, S3JournalStorageProvider>();
        }

        return builder;
    }
}
