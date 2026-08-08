using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;
using Orleans.Placement;
using Orleans.Runtime;
using Orleans.Runtime.Placement;

namespace GrainPlacement;

// <custom_placement_strategy>
[GenerateSerializer, Immutable, SuppressReferenceTracking]
public sealed class CommonKeyPlacementStrategy : PlacementStrategy
{
}

[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class CommonKeyPlacementAttribute()
    : PlacementAttribute(new CommonKeyPlacementStrategy());
// </custom_placement_strategy>

// <custom_placement_director>
public sealed class CommonKeyPlacementDirector : IPlacementDirector
{
    public Task<SiloAddress> OnAddActivation(
        PlacementStrategy strategy,
        PlacementTarget target,
        IPlacementContext context)
    {
        var compatibleSilos = context.GetCompatibleSilos(target);
        if (compatibleSilos.Length == 0)
        {
            throw new InvalidOperationException(
                $"No compatible silo is available for {target.GrainIdentity}.");
        }

        if (IPlacementDirector.GetPlacementHint(
                target.RequestContextData,
                compatibleSilos) is { } placementHint)
        {
            return Task.FromResult(placementHint);
        }

        var sortedSilos = compatibleSilos.OrderBy(silo => silo).ToArray();
        var index = target.GrainIdentity.Key.GetUniformHashCode()
            % (uint)sortedSilos.Length;

        return Task.FromResult(sortedSilos[index]);
    }
}
// </custom_placement_director>

public static class CustomPlacementConfiguration
{
    // <register_custom_placement>
    public static void AddCustomPlacement(ISiloBuilder siloBuilder)
    {
        siloBuilder.Services.AddPlacementDirector<
            CommonKeyPlacementStrategy,
            CommonKeyPlacementDirector>();
    }
    // </register_custom_placement>
}

public interface ICartGrain : IGrainWithStringKey
{
    Task Ping();
}

public interface ICartIndexGrain : IGrainWithStringKey
{
    Task Ping();
}

// <apply_custom_placement>
[CommonKeyPlacement]
public sealed class CartGrain : Grain, ICartGrain
{
    public Task Ping() => Task.CompletedTask;
}

[CommonKeyPlacement]
public sealed class CartIndexGrain : Grain, ICartIndexGrain
{
    public Task Ping() => Task.CompletedTask;
}
// </apply_custom_placement>
