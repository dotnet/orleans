using System;

namespace Orleans.Runtime.Configuration
{
    /// <summary>
    /// Statistics Configuration that are common to client and silo.
    /// </summary>
    public interface IStatisticsConfiguration
    {
        TimeSpan StatisticsMetricsTableWriteInterval { get; set; }
        TimeSpan StatisticsPerfCountersWriteInterval { get; set; }
        TimeSpan StatisticsLogWriteInterval { get; set; }
        bool StatisticsWriteLogStatisticsToTable { get; set; }
        StatisticsLevel StatisticsCollectionLevel { get; set; }

        string StatisticsProviderName { get; set; }
    }
}
