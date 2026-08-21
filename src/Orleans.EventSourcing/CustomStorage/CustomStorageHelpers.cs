using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using Orleans.Storage;

namespace Orleans.EventSourcing.CustomStorage;

internal static class CustomStorageHelpers
{
    public static ICustomStorageInterface<TState, TDelta> GetCustomStorage<TState, TDelta>(
        object hostGrain,
        GrainId grainId,
        IServiceProvider? services,
        string? providerName)
        where TState : class, new()
        where TDelta : class
    {
        if (hostGrain is ICustomStorageInterface<TState, TDelta> hostGrainCustomStorage)
        {
            return hostGrainCustomStorage;
        }

        var grainType = hostGrain.GetType();
        if (services is null || string.IsNullOrEmpty(providerName))
        {
            throw new BadProviderConfigException(
                $"Configure grain type {grainType.FullName} with an {nameof(ICustomStorageInterface<object, object>)} implementation or select a named custom storage log-consistency provider.");
        }

        var storageFactory = services.GetKeyedService<ICustomStorageFactory>(providerName);
        if (storageFactory is null)
        {
            ThrowMissingProviderException(grainType, providerName);
        }

        return storageFactory.CreateCustomStorage<TState, TDelta>(grainId);
    }

    [DoesNotReturn]
    private static void ThrowMissingProviderException(Type grainType, string providerName)
    {
        throw new BadProviderConfigException(
            $"Custom storage log-consistency provider \"{providerName}\" requires a keyed {nameof(ICustomStorageFactory)} registration for grain type {grainType.FullName}.");
    }
}
