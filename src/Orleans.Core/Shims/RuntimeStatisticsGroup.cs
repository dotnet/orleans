using System;
using Microsoft.Extensions.Logging;
using Orleans.Statistics;

namespace Orleans.Runtime
{
    internal class DummyHostEnvironmentStatistics : IHostEnvironmentStatistics
    {
        public long TotalPhysicalMemory => int.MaxValue;

        public float CpuUsage => 0;

        public long AvailableMemory => TotalPhysicalMemory;

        public DummyHostEnvironmentStatistics(ILoggerFactory loggerFactory)
        {
            var logger = loggerFactory.CreateLogger<DummyHostEnvironmentStatistics>();
            logger.Warn(ErrorCode.PerfCounterNotRegistered,
                "No implementation of IHostEnvironmentStatistics was found. Load shedding will not work yet");
        }
    }
}
