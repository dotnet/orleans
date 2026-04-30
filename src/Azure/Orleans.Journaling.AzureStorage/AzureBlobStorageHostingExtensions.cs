using Microsoft.Extensions.DependencyInjection;
using Orleans.Configuration.Internal;
using Orleans.Runtime;
using Orleans.Hosting;

namespace Orleans.Journaling;

public static class AzureBlobStorageHostingExtensions
{
    public static ISiloBuilder AddAzureAppendBlobLogStorage(this ISiloBuilder builder) => builder.AddAzureAppendBlobLogStorage(configure: null);
    public static ISiloBuilder AddAzureAppendBlobLogStorage(this ISiloBuilder builder, Action<AzureAppendBlobLogStorageOptions>? configure)
    {
        builder.AddLogStorage();

        var services = builder.Services;

        var options = builder.Services.AddOptions<AzureAppendBlobLogStorageOptions>();
        if (configure is not null)
        {
            options.Configure(configure);
        }

        if (services.Any(service => service.ServiceType.Equals(typeof(AzureAppendBlobLogStorageProvider))))
        {
            return builder;
        }

        builder.Services.AddSingleton<AzureAppendBlobLogStorageProvider>();
        builder.Services.AddFromExisting<ILogStorageProvider, AzureAppendBlobLogStorageProvider>();
        builder.Services.AddFromExisting<ILogFormatKeyProvider, AzureAppendBlobLogStorageProvider>();
        builder.Services.AddFromExisting<ILifecycleParticipant<ISiloLifecycle>, AzureAppendBlobLogStorageProvider>();
        return builder;
    }
}
