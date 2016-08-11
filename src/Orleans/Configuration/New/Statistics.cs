using System;
using System.Text;

namespace Orleans.Runtime.Configuration.New
{
    /// <summary>
    /// Statistics Configuration that are common to client and silo.
    /// </summary>
    public class Statistics
    {
        private static readonly TimeSpan DEFAULT_STATS_METRICS_TABLE_WRITE_PERIOD = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan DEFAULT_STATS_PERF_COUNTERS_WRITE_PERIOD = Constants.INFINITE_TIMESPAN;
        private static readonly TimeSpan DEFAULT_STATS_LOG_WRITE_PERIOD = TimeSpan.FromMinutes(5);

        public Statistics()
        {
            StatisticsProviderName = null;
            StatisticsMetricsTableWriteInterval = DEFAULT_STATS_METRICS_TABLE_WRITE_PERIOD;
            StatisticsPerfCountersWriteInterval = DEFAULT_STATS_PERF_COUNTERS_WRITE_PERIOD;
            StatisticsLogWriteInterval = DEFAULT_STATS_LOG_WRITE_PERIOD;
            StatisticsWriteLogStatisticsToTable = true;
            StatisticsCollectionLevel = NodeConfiguration.DEFAULT_STATS_COLLECTION_LEVEL;
        }
        public TimeSpan StatisticsMetricsTableWriteInterval { get; set; }
        public TimeSpan StatisticsPerfCountersWriteInterval { get; set; }
        public TimeSpan StatisticsLogWriteInterval { get; set; }
        public bool StatisticsWriteLogStatisticsToTable { get; set; }
        public StatisticsLevel StatisticsCollectionLevel { get; set; }

        public string StatisticsProviderName { get; set; }

        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.Append("   Statistics: ").AppendLine();
            sb.Append("     MetricsTableWriteInterval: ").Append(StatisticsMetricsTableWriteInterval).AppendLine();
            sb.Append("     PerfCounterWriteInterval: ").Append(StatisticsPerfCountersWriteInterval).AppendLine();
            sb.Append("     LogWriteInterval: ").Append(StatisticsLogWriteInterval).AppendLine();
            sb.Append("     WriteLogStatisticsToTable: ").Append(StatisticsWriteLogStatisticsToTable).AppendLine();
            sb.Append("     StatisticsCollectionLevel: ").Append(StatisticsCollectionLevel).AppendLine();
#if TRACK_DETAILED_STATS
            sb.Append("     TRACK_DETAILED_STATS: true").AppendLine();
#endif
            if (!string.IsNullOrEmpty(StatisticsProviderName))
                sb.Append("     StatisticsProviderName:").Append(StatisticsProviderName).AppendLine();
            return sb.ToString();
        }
    }
}