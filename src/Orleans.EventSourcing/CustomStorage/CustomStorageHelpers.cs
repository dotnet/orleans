using System;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Orleans.EventSourcing.CustomStorage;
using Orleans.Runtime;

namespace OrleansEventSourcing.CustomStorage;

public static class CustomStorageHelpers
{
    public static ICustomStorageInterface<TState, TDelta> GetCustomStorage<TState, TDelta>(object hostGrain, GrainId grainId, IServiceProvider services)
        where TState : class, new()
        where TDelta : class
    {
        ArgumentNullException.ThrowIfNull(hostGrain);

        if (hostGrain is ICustomStorageInterface<TState, TDelta> hostGrainCustomStorage)
        {
            return hostGrainCustomStorage;
        }

        var grainType = hostGrain.GetType();
        var attrs = grainType.GetCustomAttributes(typeof(CustomStorageProviderAttribute), true);
        var attr = attrs.Length > 0 ? (CustomStorageProviderAttribute)attrs[0] : null;
        var storageFactory = attr != null
            ? services.GetKeyedService<ICustomStorageFactory>(attr.ProviderName)
            : services.GetService<ICustomStorageFactory>();

        if (storageFactory == null)
        {
            ThrowMissingProviderException(grainType, attr?.ProviderName);
        }

        return storageFactory.CreateCustomStorage<TState, TDelta>(grainId);
    }

    [DoesNotReturn]
    private static void ThrowMissingProviderException(Type grainType, string name)
    {
        var grainTypeName = grainType.FullName;
        var errMsg = string.IsNullOrEmpty(name)
            ? $"No default custom storage provider found loading grain type {grainTypeName} and grain does not implement ICustomStorageInterface."
            : $"No custom storage provider named \"{name}\" found loading grain type {grainTypeName} and grain does not implement ICustomStorageInterface.";
        throw new BadCustomStorageProviderConfigException(errMsg);
    }
}