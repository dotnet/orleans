using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Configuration;

namespace Orleans.Storage;

public static class DynamoDBGrainStorageFactory
{
    public static IGrainStorage Create(IServiceProvider services, string name)
    {
        var optionsMonitor = services.GetRequiredService<IOptionsMonitor<DynamoDBStorageOptions>>();
        return ActivatorUtilities.CreateInstance<DynamoDBGrainStorage>(services, optionsMonitor.Get(name), name);
    }
}