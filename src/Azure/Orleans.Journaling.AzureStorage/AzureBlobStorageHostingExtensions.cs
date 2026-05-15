using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration.Internal;

namespace Orleans.Journaling;

public static class AzureBlobStorageHostingExtensions
{
    public static ISiloBuilder AddAzureBlobJournalStorage(this ISiloBuilder builder) => builder.AddAzureBlobJournalStorage(configure: null);
    public static ISiloBuilder AddAzureBlobJournalStorage(this ISiloBuilder builder, Action<AzureBlobJournalStorageOptions>? configure)
    {
        builder.AddJournalStorage();

        var services = builder.Services;

        var options = builder.Services.AddOptions<AzureBlobJournalStorageOptions>();
        if (configure is not null)
        {
            options.Configure(configure);
        }

        if (!services.Any(service => service.ServiceType.Equals(typeof(AzureBlobJournalStorageProvider))))
        {
            builder.Services.AddSingleton<AzureBlobJournalStorageProvider>();
            builder.Services.AddFromExisting<IJournalStorageProvider, AzureBlobJournalStorageProvider>();
            builder.Services.AddFromExisting<ILifecycleParticipant<ISiloLifecycle>, AzureBlobJournalStorageProvider>();
        }
        return builder;
    }
}
