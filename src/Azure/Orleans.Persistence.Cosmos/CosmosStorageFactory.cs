// <copyright file="CosmosStorageFactory.cs" company="Microsoft">
// Copyright (c) Microsoft. All rights reserved.
// </copyright>

using Microsoft.Extensions.DependencyInjection;
using Orleans.Persistence.Cosmos.TypeInfo;
using Orleans.Storage;

namespace Orleans.Persistence.Cosmos;

/// <summary>
/// Creates <see cref="CosmosGrainStorage"/> instances.
/// </summary>
public static class CosmosStorageFactory
{
    /// <summary>
    /// Creates a <see cref="CosmosGrainStorage"/> instance with the specified name.
    /// </summary>
    /// <param name="services">The service provider.</param>
    /// <param name="name">The name.</param>
    /// <returns>A new <see cref="CosmosGrainStorage"/> instance.</returns>
    public static IGrainStorage Create(IServiceProvider services, string name)
    {
        var grainStateTypeInfoProvider = services.GetRequiredService<IGrainStateTypeInfoProvider>();
        return Create(services, name, grainStateTypeInfoProvider);
    }

    /// <summary>
    /// Creates a <see cref="CosmosGrainStorage"/> instance with the specified name.
    /// </summary>
    /// <param name="services">The service provider.</param>
    /// <param name="name">The name.</param>
    /// <param name="grainStateTypeInfoProvider">provider for grainStateTypeInfo (probably based on activation context)</param>
    /// <returns>A new <see cref="CosmosGrainStorage"/> instance.</returns>
    internal static IGrainStorage Create(
        IServiceProvider services,
        string name,
        IGrainStateTypeInfoProvider grainStateTypeInfoProvider)
    {
        var optionsMonitor = services.GetRequiredService<IOptionsMonitor<CosmosGrainStorageOptions>>();
        var idProvider = services.GetServiceByName<IDocumentIdProvider>(name) ?? services.GetRequiredService<IDocumentIdProvider>();

        return new CosmosGrainStorage(
            name,
            optionsMonitor.Get(name),
            services,
            idProvider,
            grainStateTypeInfoProvider);
    }
}