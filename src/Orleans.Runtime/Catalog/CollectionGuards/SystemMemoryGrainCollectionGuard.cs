using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Statistics;

namespace Orleans.Runtime.CollectionGuards;

/// <summary>
/// A guard that stops collection of grains until the system memory
/// is under a certain load.
/// </summary>
public class SystemMemoryGrainCollectionGuard : IGrainCollectionGuard
{
    private readonly IHostEnvironmentStatistics _hostEnvironmentStatistics;
    private readonly IOptions<GrainCollectionOptions> _grainCollectionOptions;

    public SystemMemoryGrainCollectionGuard(
        IHostEnvironmentStatistics hostEnvironmentStatistics,
        IOptions<GrainCollectionOptions> grainCollectionOptions)
    {
        _hostEnvironmentStatistics = hostEnvironmentStatistics;
        _grainCollectionOptions = grainCollectionOptions;
    }

    private float MemoryPercentageAvailable =>
        _hostEnvironmentStatistics.AvailableMemory.HasValue == false
        || _hostEnvironmentStatistics.TotalPhysicalMemory.HasValue ==  false
            ? 0
            : _hostEnvironmentStatistics.AvailableMemory.Value
              / (float)_hostEnvironmentStatistics.TotalPhysicalMemory.Value;

    public bool ShouldCollect() =>
        (_grainCollectionOptions.Value.CollectionSystemMemoryFreeThreshold.HasValue == false
         && _grainCollectionOptions.Value.CollectionSystemMemoryFreePercentThreshold.HasValue == false)
        || (_grainCollectionOptions.Value.CollectionSystemMemoryFreeThreshold.HasValue == true
            && _grainCollectionOptions.Value.CollectionSystemMemoryFreeThreshold.Value == 0)
        || _hostEnvironmentStatistics.AvailableMemory.HasValue == false
        || (_grainCollectionOptions.Value.CollectionSystemMemoryFreeThreshold.HasValue
            && _hostEnvironmentStatistics.AvailableMemory < _grainCollectionOptions.Value.CollectionSystemMemoryFreeThreshold.Value)
        || _grainCollectionOptions.Value.CollectionSystemMemoryFreePercentThreshold.HasValue
            && (MemoryPercentageAvailable * 100)
               < _grainCollectionOptions.Value.CollectionSystemMemoryFreePercentThreshold;
}