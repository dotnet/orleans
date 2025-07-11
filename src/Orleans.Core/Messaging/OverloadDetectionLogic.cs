using Orleans.Configuration;
using Orleans.Statistics;

namespace Orleans.Core.Messaging;

internal static class OverloadDetectionLogic
{
    /// <summary>
    /// Determines whether or not the process is overloaded.
    /// </summary>
    /// <remarks><see cref="LoadSheddingOptions.LoadSheddingEnabled"/> is ignored here.</remarks>
    public static bool IsOverloaded(ref readonly EnvironmentStatistics statistics, LoadSheddingOptions options)
    {
        bool isMemoryOverloaded = statistics.MemoryUsagePercentage > options.MemoryThreshold;
        bool isCpuOverloaded = statistics.FilteredCpuUsagePercentage > options.CpuThreshold;

        return isMemoryOverloaded || isCpuOverloaded;
    }
}
