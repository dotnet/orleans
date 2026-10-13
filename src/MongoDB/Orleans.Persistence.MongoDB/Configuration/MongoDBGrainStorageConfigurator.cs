using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans.Providers.MongoDB.StorageProviders.Serializers;
using System;

namespace Orleans.Providers.MongoDB.Configuration;

internal class MongoDBGrainStorageConfigurator : IPostConfigureOptions<MongoDBGrainStorageOptions>
{
    private readonly IServiceProvider _serviceProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="MongoDBGrainStorageConfigurator"/> class.
    /// </summary>
    /// <param name="serviceProvider">The service provider.</param>
    public MongoDBGrainStorageConfigurator(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    /// <inheritdoc/>
    public void PostConfigure(string? name, MongoDBGrainStorageOptions options)
    {
        if (options.GrainStateSerializer == default)
        {
            options.GrainStateSerializer = _serviceProvider.GetKeyedService<IGrainStateSerializer>(name) ?? _serviceProvider.GetRequiredService<IGrainStateSerializer>();
        }
    }
}
