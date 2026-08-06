using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration.Overrides;
using Orleans.Storage;

namespace GrainStorage;

internal static class FileGrainStorageFactory
{
    internal static IGrainStorage Create(
        IServiceProvider services, string name)
    {
        var optionsMonitor =
            services.GetRequiredService<IOptionsMonitor<FileGrainStorageOptions>>();

        return ActivatorUtilities.CreateInstance<FileGrainStorage>(
            services,
            name,
            optionsMonitor.Get(name),
            services.GetProviderClusterOptions(name));
    }
}
