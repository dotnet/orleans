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
    public static bool IsOverloaded(IEnvironmentStatistics statistics, LoadSheddingOptions options)
    {
        var info = GC.GetGCMemoryInfo();
        var maximumMemoryLimit = Math.Min(info.TotalAvailableMemoryBytes, info.HighMemoryLoadThresholdBytes);

        bool isMemoryOverloaded = info.MemoryLoadBytes >= maximumMemoryLimit * options.MemoryLoadLimit / 100.0d;
        bool isCpuOverloaded = statistics.GetHardwareStatistics().CpuUsagePercentage > options.LoadSheddingLimit;

        return isMemoryOverloaded || isCpuOverloaded;
    }
}