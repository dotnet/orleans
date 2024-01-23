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
        var maxProcessMemoryBytes = statistics.MaximumAvailableMemoryBytes;
        var minFreeMemoryFraction = (100 - options.MemoryThreshold) / 100d;
        var minFreeMemoryBytes = (long)(maxProcessMemoryBytes * minFreeMemoryFraction); // represents the minimum amount of memory that should remain available

        bool isMemoryOverloaded = statistics.AvailableMemoryBytes < minFreeMemoryBytes;
        bool isCpuOverloaded = statistics.CpuUsagePercentage > options.CpuThreshold;

        return isMemoryOverloaded || isCpuOverloaded;
    }
}