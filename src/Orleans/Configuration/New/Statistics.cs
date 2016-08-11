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
            ProviderName = null;
            MetricsTableWriteInterval = DEFAULT_STATS_METRICS_TABLE_WRITE_PERIOD;
            PerfCountersWriteInterval = DEFAULT_STATS_PERF_COUNTERS_WRITE_PERIOD;
            LogWriteInterval = DEFAULT_STATS_LOG_WRITE_PERIOD;
            WriteLogStatisticsToTable = true;
            CollectionLevel = NodeConfiguration.DEFAULT_STATS_COLLECTION_LEVEL;
        }
        public TimeSpan MetricsTableWriteInterval { get; set; }
        public TimeSpan PerfCountersWriteInterval { get; set; }
        public TimeSpan LogWriteInterval { get; set; }
        public bool WriteLogStatisticsToTable { get; set; }
        public StatisticsLevel CollectionLevel { get; set; }

        public string ProviderName { get; set; }

        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.Append("   Statistics: ").AppendLine();
            sb.Append("     MetricsTableWriteInterval: ").Append(MetricsTableWriteInterval).AppendLine();
            sb.Append("     PerfCounterWriteInterval: ").Append(PerfCountersWriteInterval).AppendLine();
            sb.Append("     LogWriteInterval: ").Append(LogWriteInterval).AppendLine();
            sb.Append("     WriteLogStatisticsToTable: ").Append(WriteLogStatisticsToTable).AppendLine();
            sb.Append("     StatisticsCollectionLevel: ").Append(CollectionLevel).AppendLine();
#if TRACK_DETAILED_STATS
            sb.Append("     TRACK_DETAILED_STATS: true").AppendLine();
#endif
            if (!string.IsNullOrEmpty(ProviderName))
                sb.Append("     StatisticsProviderName:").Append(ProviderName).AppendLine();
            return sb.ToString();
        }
    }
}