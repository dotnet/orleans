using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Configuration.Internal;
using Orleans.Runtime;
using Orleans.Hosting;

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
            builder.Services.AddFromExisting<ILifecycleParticipant<ISiloLifecycle>, AzureBlobJournalStorageProvider>();
        }

        builder.Services.TryAddScoped<IJournalStorage>(static sp =>
            sp.GetRequiredService<AzureBlobJournalStorageProvider>().Create(sp.GetRequiredService<IGrainContext>()));
        return builder;
    }
}
