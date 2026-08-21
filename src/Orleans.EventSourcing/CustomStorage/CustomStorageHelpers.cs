using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using Orleans.Storage;

namespace Orleans.EventSourcing.CustomStorage;

internal static class CustomStorageHelpers
{
    public static ICustomStorageInterface<TState, TDelta> GetCustomStorage<TState, TDelta>(object hostGrain, GrainId grainId, IServiceProvider? services)
        where TState : class, new()
        where TDelta : class
    {
        if (hostGrain is ICustomStorageInterface<TState, TDelta> hostGrainCustomStorage)
        {
            return hostGrainCustomStorage;
        }

        var grainType = hostGrain.GetType();
        if (services is null)
        {
            throw new BadProviderConfigException(
                $"Construct {nameof(LogConsistencyProvider)} through dependency injection to configure an {nameof(ICustomStorageFactory)} for grain type {grainType.FullName}.");
        }

        var attr = grainType.GetCustomAttributes(typeof(CustomStorageProviderAttribute), true)
            .OfType<CustomStorageProviderAttribute>()
            .FirstOrDefault();
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
    private static void ThrowMissingProviderException(Type grainType, string? name)
    {
        var grainTypeName = grainType.FullName;
        var errMsg = string.IsNullOrEmpty(name)
            ? $"Configure grain type {grainTypeName} with an {nameof(ICustomStorageInterface<object, object>)} implementation or register a default {nameof(ICustomStorageFactory)}."
            : $"Register an {nameof(ICustomStorageFactory)} named \"{name}\" for grain type {grainTypeName}.";
        throw new BadProviderConfigException(errMsg);
    }
}
