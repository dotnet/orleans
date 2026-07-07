using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Orleans.Configuration.Internal;

namespace Orleans.Journaling;

public static class AzureTableStorageHostingExtensions
{
    public static ISiloBuilder AddAzureTableJournalStorage(this ISiloBuilder builder) => builder.AddAzureTableJournalStorage(configure: null);
    public static ISiloBuilder AddAzureTableJournalStorage(this ISiloBuilder builder, Action<AzureTableJournalStorageOptions>? configure)
    {
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
