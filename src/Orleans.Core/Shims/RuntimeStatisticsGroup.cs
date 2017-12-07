using System;
using Microsoft.Extensions.Logging;

namespace Orleans.Runtime
{
    internal class RuntimeStatisticsGroup : IDisposable
    {
        private readonly ILogger logger;
        public long MemoryUsage => 0;
        public long TotalPhysicalMemory => int.MaxValue;
        public long AvailableMemory => TotalPhysicalMemory;
        public float CpuUsage => 0;

        public RuntimeStatisticsGroup(ILoggerFactory loggerFactory)
        {
            this.logger = loggerFactory.CreateLogger<RuntimeStatisticsGroup>();
        }

        internal void Start()
        {

            logger.Warn(ErrorCode.PerfCounterNotRegistered,
                "CPU & Memory perf counters are not available in .NET Standard. Load shedding will not work yet");
        }

        public void Stop()
        {
        }

        public void Dispose()
        {
        }
    }
}
