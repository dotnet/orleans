using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;
using Orleans.Metadata;
using Orleans.Placement;
using Orleans.Runtime;
using Orleans.Runtime.MembershipService.SiloMetadata;
using Orleans.Runtime.Placement;

namespace GrainPlacement;

// <custom_placement_filter_strategy>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public sealed class MinimumSiloCoresPlacementFilterAttribute(
    int minimumCores,
    int order = 0)
    : PlacementFilterAttribute(
        new MinimumSiloCoresPlacementFilterStrategy(minimumCores, order));

public sealed class MinimumSiloCoresPlacementFilterStrategy(
    int minimumCores,
    int order)
    : PlacementFilterStrategy(order)
{
    private const string MinimumCoresProperty = "minimum-cores";

    public MinimumSiloCoresPlacementFilterStrategy()
        : this(1, 0)
    {
    }

    public int MinimumCores { get; private set; }
        = ValidateMinimumCores(minimumCores);

    public override void AdditionalInitialize(GrainProperties properties)
    {
        var value = GetPlacementFilterGrainProperty(
            MinimumCoresProperty,
            properties);

        if (!int.TryParse(
                value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var parsedValue))
        {
            throw new ArgumentException(
                $"Invalid {MinimumCoresProperty} property value.");
        }

        MinimumCores = ValidateMinimumCores(parsedValue);
    }

    protected override IEnumerable<KeyValuePair<string, string>>
        GetAdditionalGrainProperties(
            IServiceProvider services,
            Type grainClass,
            GrainType grainType,
            IReadOnlyDictionary<string, string> existingProperties)
    {
        yield return new(
            MinimumCoresProperty,
            MinimumCores.ToString(CultureInfo.InvariantCulture));
    }

    private static int ValidateMinimumCores(int value)
        => value > 0
            ? value
            : throw new ArgumentOutOfRangeException(
                nameof(value),
                "The minimum core count must be positive.");
}
// </custom_placement_filter_strategy>

// <custom_placement_filter_director>
public sealed class MinimumSiloCoresPlacementFilterDirector(
    ISiloMetadataCache siloMetadataCache)
    : IPlacementFilterDirector
{
    private const string SiloCoresMetadataKey = "hardware.cores";

    public IEnumerable<SiloAddress> Filter(
        PlacementFilterStrategy filterStrategy,
        PlacementTarget target,
        IEnumerable<SiloAddress> silos)
    {
        if (filterStrategy
            is not MinimumSiloCoresPlacementFilterStrategy strategy)
        {
            throw new ArgumentException(
                $"Expected {nameof(MinimumSiloCoresPlacementFilterStrategy)}.",
                nameof(filterStrategy));
        }

        return silos.Where(silo =>
        {
            var metadata = siloMetadataCache.GetSiloMetadata(silo).Metadata;
            return metadata.TryGetValue(
                    SiloCoresMetadataKey,
                    out var value)
                && int.TryParse(
                    value,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var coreCount)
                && coreCount >= strategy.MinimumCores;
        });
    }
}
// </custom_placement_filter_director>

public static class CustomPlacementFilterConfiguration
{
    // <register_custom_placement_filter>
    public static void AddCustomPlacementFilter(ISiloBuilder siloBuilder)
    {
        siloBuilder.Services.AddPlacementFilter<
            MinimumSiloCoresPlacementFilterStrategy,
            MinimumSiloCoresPlacementFilterDirector>(
                ServiceLifetime.Transient);
    }
    // </register_custom_placement_filter>
}

public interface IComputeGrain : IGrainWithStringKey
{
    Task Ping();
}

// <apply_custom_placement_filter>
[MinimumSiloCoresPlacementFilter(minimumCores: 16)]
[ResourceOptimizedPlacement]
public sealed class ComputeGrain : Grain, IComputeGrain
{
    public Task Ping() => Task.CompletedTask;
}
// </apply_custom_placement_filter>
