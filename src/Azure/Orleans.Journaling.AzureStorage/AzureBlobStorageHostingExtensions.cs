using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration.Internal;
using Orleans.Runtime;
using Orleans.Hosting;

namespace Orleans.Journaling;

public static class AzureBlobStorageHostingExtensions
{
    public static ISiloBuilder AddAzureAppendBlobJournalStorage(this ISiloBuilder builder) => builder.AddAzureAppendBlobJournalStorage(configure: null);
    public static ISiloBuilder AddAzureAppendBlobJournalStorage(this ISiloBuilder builder, Action<AzureAppendBlobJournalStorageOptions>? configure)
    {
        builder.AddJournalStorage();

        var services = builder.Services;

        var options = builder.Services.AddOptions<AzureAppendBlobJournalStorageOptions>();
        if (configure is not null)
        {
            options.Configure(configure);
        }

        if (services.Any(service => service.ServiceType.Equals(typeof(AzureAppendBlobJournalStorageProvider))))
        {
            return builder;
        }

        builder.Services.AddSingleton<AzureAppendBlobJournalStorageProvider>();
        builder.Services.AddFromExisting<IJournalStorageProvider, AzureAppendBlobJournalStorageProvider>();
        builder.Services.AddFromExisting<ILifecycleParticipant<ISiloLifecycle>, AzureAppendBlobJournalStorageProvider>();
        return builder;
    }
}
