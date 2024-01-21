using System;
using Orleans.Configuration;
using Orleans.Statistics;

namespace Orleans.Core.Messaging;

internal static class OverloadDetectionLogic
{
    /// <summary>
    /// Determines whether or not the process is overloaded.
    /// </summary>
    /// <remarks><see cref="LoadSheddingOptions.LoadSheddingEnabled"/> is ignored.</remarks>
    public static bool Determine(IEnvironmentStatistics statistics, LoadSheddingOptions options)
    {
        var info = GC.GetGCMemoryInfo();

        bool isMemoryOverloaded = info.MemoryLoadBytes >= options.MemoryLoadLimit * Math.Min(info.TotalAvailableMemoryBytes, info.HighMemoryLoadThresholdBytes);
        bool isCpuOverloaded = statistics.CpuUsagePercentage > options.LoadSheddingLimit;

        return isMemoryOverloaded || isCpuOverloaded;
    }
}