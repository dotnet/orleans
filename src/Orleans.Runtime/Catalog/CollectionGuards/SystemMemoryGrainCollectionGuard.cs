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

    // Calculate current memory usage as a percentage of the total memory available,
    // erring on the side of caution by assuming that the memory is full if there
    // are missing values reported through the IHostEnvironmentStatistics interface.
    private float MemoryPercentageAvailable =>
        _hostEnvironmentStatistics.AvailableMemory.HasValue == false
        || _hostEnvironmentStatistics.TotalPhysicalMemory.HasValue ==  false
            ? 0
            : _hostEnvironmentStatistics.AvailableMemory.Value
              / (float)_hostEnvironmentStatistics.TotalPhysicalMemory.Value;

    public bool ShouldCollect()
    {
        // Check to see if we have configuration
        if (_grainCollectionOptions.Value.CollectionSystemMemoryFreeThreshold.HasValue == false
            && _grainCollectionOptions.Value.CollectionSystemMemoryFreePercentThreshold.HasValue == false)
        {
            return true;
        }

        // Check to see if we have configuration that wants to immediately collect
        if (_grainCollectionOptions.Value.CollectionSystemMemoryFreeThreshold.HasValue == true
            && _grainCollectionOptions.Value.CollectionSystemMemoryFreeThreshold.Value == 0)
        {
            return true;
        }

        // Check to see if we have system stats
        if (_hostEnvironmentStatistics.AvailableMemory.HasValue == false)
        {
            return true;
        }

        // If we have config and statistics available for absolute memory, see if we should collect
        if (_grainCollectionOptions.Value.CollectionSystemMemoryFreeThreshold.HasValue
             && _hostEnvironmentStatistics.AvailableMemory < _grainCollectionOptions.Value.CollectionSystemMemoryFreeThreshold.Value)
        {
            return true;
        }

        // If we have config and statistics available for percentage memory, see if we should collect
        if (_grainCollectionOptions.Value.CollectionSystemMemoryFreePercentThreshold.HasValue
            && (MemoryPercentageAvailable * 100)
            < _grainCollectionOptions.Value.CollectionSystemMemoryFreePercentThreshold)
        {
            return true;
        }

        // If none of the above conditions are met, we should not collect
        return false;
    }
}