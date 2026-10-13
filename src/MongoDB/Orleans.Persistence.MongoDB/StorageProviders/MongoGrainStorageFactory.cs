using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Persistence.MongoDB;
using Orleans.Storage;

namespace Orleans.Providers.MongoDB.StorageProviders;

internal static class MongoGrainStorageFactory
{
    public static IGrainStorage Create(IServiceProvider services, string name)
    {
        var optionsMonitor = services.GetRequiredService<IOptionsMonitor<MongoDBGrainStorageOptions>>();

        var options = optionsMonitor.Get(name);
        return ActivatorUtilities.CreateInstance<MongoGrainStorage>(services, services.GetMongoClient(options), options);
    }
}
