using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Providers.MongoDB.Configuration;
using Orleans.Storage;

namespace Orleans.Providers.MongoDB.StorageProviders;

internal static class MongoGrainStorageFactory
{
    public static IGrainStorage Create(IServiceProvider services, string name)
    {
        var optionsMonitor = services.GetRequiredService<IOptionsMonitor<MongoDBGrainStorageOptions>>();

        return ActivatorUtilities.CreateInstance<MongoGrainStorage>(services, optionsMonitor.Get(name));
    }
}
