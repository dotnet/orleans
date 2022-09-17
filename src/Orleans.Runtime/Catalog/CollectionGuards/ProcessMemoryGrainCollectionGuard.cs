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

    public bool ShouldCollect() =>
        _appEnvironmentStatistics.MemoryUsage.HasValue == false
        || _grainCollectionOptions.Value.CollectionGCMemoryThreshold.HasValue == false
        || _appEnvironmentStatistics.MemoryUsage.Value
           > _grainCollectionOptions.Value.CollectionGCMemoryThreshold;
}