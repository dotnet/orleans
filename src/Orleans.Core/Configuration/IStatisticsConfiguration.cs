using System;

namespace Orleans.Runtime.Configuration
{
    /// <summary>
    /// The level of runtime statistics to collect and report periodically.
    /// The default level is Info.
    /// </summary>
    public enum StatisticsLevel
    {
        Critical,
        Info,
        Verbose,
        Verbose2,
        Verbose3,
    }

    /// <summary>
    /// Statistics Configuration that are common to client and silo.
    /// </summary>
    public interface IStatisticsConfiguration
    {
        TimeSpan StatisticsPerfCountersWriteInterval { get; set; }
        TimeSpan StatisticsLogWriteInterval { get; set; }
        StatisticsLevel StatisticsCollectionLevel { get; set; }
    }
}
