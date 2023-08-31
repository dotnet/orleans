using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Statistics;

namespace Orleans.Runtime.CollectionGuards;

/// <summary>
/// A guard that stops collection of grains if the memory
/// pressure is low.
/// </summary>
public class ProcessMemoryGrainCollectionGuard : IGrainCollectionGuard
{
    private readonly IAppEnvironmentStatistics _appEnvironmentStatistics;
    private readonly IOptions<GrainCollectionOptions> _grainCollectionOptions;

    public ProcessMemoryGrainCollectionGuard(
        IAppEnvironmentStatistics appEnvironmentStatistics,
        IOptions<GrainCollectionOptions> grainCollectionOptions)
    {
        _appEnvironmentStatistics = appEnvironmentStatistics;
        _grainCollectionOptions = grainCollectionOptions;
    }

    public bool ShouldCollect()
    {
        var memoryUsage = _appEnvironmentStatistics.MemoryUsage;

        // If we do not have memory usage stats, collect activations
        if (memoryUsage.HasValue == false)
        {
            return true;
        }

        // If we do not have a GC limit, collect activations
        if (_grainCollectionOptions.Value.CollectionGCMemoryThreshold.HasValue == false)
        {
            return true;
        }

        // Run collection if current usage is above the limit
        return memoryUsage > _grainCollectionOptions.Value.CollectionGCMemoryThreshold;
    }
}