using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Configuration;
using Orleans.Hosting;
using Orleans.Placement;
using Orleans.Runtime;
using Orleans.Runtime.MembershipService.SiloMetadata;
using Orleans.Runtime.Placement;
using Orleans.Runtime.Placement.Filtering;
using System.Diagnostics.CodeAnalysis;

namespace Documentation.Grains.Placement.Filtering
{
    public interface IPremiumZoneGrain : IGrainWithStringKey;

    public interface ILocalityGrain : IGrainWithStringKey;

    public interface IOrderedFilterGrain : IGrainWithStringKey;

    internal static class MetadataConfiguration
    {
        internal static void Configure(ISiloBuilder siloBuilder)
        {
            // <configure_silo_metadata>
siloBuilder.UseSiloMetadata(
    new Dictionary<string, string>
    {
        ["zone"] = "west-1",
        ["tier"] = "premium"
    });
            // </configure_silo_metadata>
        }
    }

    // <required_metadata_filter>
[Experimental("ORLEANSEXP004")]
[RequiredMatchSiloMetadataPlacementFilter(
    ["zone", "tier"])]
public sealed class PremiumZoneGrain :
    Grain,
    IPremiumZoneGrain
{
}
    // </required_metadata_filter>

    // <preferred_metadata_filter>
[Experimental("ORLEANSEXP004")]
[PreferredMatchSiloMetadataPlacementFilter(
    ["rack", "zone"],
    minCandidates: 2)]
public sealed class LocalityGrain :
    Grain,
    ILocalityGrain
{
}
    // </preferred_metadata_filter>

    // <ordered_metadata_filters>
[Experimental("ORLEANSEXP004")]
[RequiredMatchSiloMetadataPlacementFilter(
    ["tier"],
    order: 0)]
[PreferredMatchSiloMetadataPlacementFilter(
    ["rack", "zone"],
    minCandidates: 2,
    order: 10)]
public sealed class OrderedFilterGrain :
    Grain,
    IOrderedFilterGrain
{
}
    // </ordered_metadata_filters>
}

namespace Documentation.Grains.Placement.Strategies
{
    public interface IGatewayCacheGrain : IGrainWithStringKey;

    public interface IHardwareSessionGrain : IGrainWithStringKey;

    [Experimental("ORLEANSEXP001")]
    internal static class PlacementConfiguration
    {
        internal static void ConfigureResourceOptimized(
            ISiloBuilder siloBuilder)
        {
            // <configure_resource_optimized_placement>
siloBuilder.Configure<ResourceOptimizedPlacementOptions>(options =>
{
    options.CpuUsageWeight = 40;
    options.MemoryUsageWeight = 20;
    options.AvailableMemoryWeight = 20;
    options.ActivationCountWeight = 15;
    options.LocalSiloPreferenceMargin = 5;
});
            // </configure_resource_optimized_placement>
        }

        internal static void ConfigureRandom(ISiloBuilder siloBuilder)
        {
            // <configure_random_placement>
siloBuilder.Services.AddSingleton<
    PlacementStrategy,
    RandomPlacement>();
            // </configure_random_placement>
        }

        [Experimental("ORLEANSEXP002")]
        internal static void ConfigureRebalancing(ISiloBuilder siloBuilder)
        {
            // <configure_activation_rebalancing>
siloBuilder.AddActivationRepartitioner();

siloBuilder.AddActivationRebalancer();
            // </configure_activation_rebalancing>
        }

        internal static void ConfigureLoadShedding(ISiloBuilder siloBuilder)
        {
            // <configure_load_shedding>
siloBuilder.Configure<LoadSheddingOptions>(options =>
{
    options.LoadSheddingEnabled = true;
    options.CpuThreshold = 90;
    options.MemoryThreshold = 85;
});
            // </configure_load_shedding>
        }
    }

    // <prefer_local_grain>
[PreferLocalPlacement]
public sealed class GatewayCacheGrain :
    Grain,
    IGatewayCacheGrain
{
}
    // </prefer_local_grain>

    internal sealed class MigratingGrain : Grain
    {
        // <move_grain>
public Task Move()
{
    MigrateOnIdle();
    return Task.CompletedTask;
}
        // </move_grain>
    }

    // <immovable_grain>
[Immovable]
public sealed class HardwareSessionGrain :
    Grain,
    IHardwareSessionGrain
{
}
    // </immovable_grain>
}
