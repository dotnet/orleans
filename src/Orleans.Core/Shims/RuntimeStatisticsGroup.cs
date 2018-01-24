using System;
using Microsoft.Extensions.Logging;
using Orleans.Statistics;

namespace Orleans.Runtime
{
    internal class NoOpHostEnvironmentStatistics : IHostEnvironmentStatistics
    {
        public long? TotalPhysicalMemory => null;

        public float? CpuUsage => null;

        public long? AvailableMemory => null;

        public NoOpHostEnvironmentStatistics(ILoggerFactory loggerFactory)
        {
            var logger = loggerFactory.CreateLogger<NoOpHostEnvironmentStatistics>();
            logger.Warn(ErrorCode.PerfCounterNotRegistered,
                "No implementation of IHostEnvironmentStatistics was found. Load shedding will not work yet");
        }
    }
}
