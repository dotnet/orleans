namespace Orleans.Runtime
{
    internal class RuntimeStatisticsGroup
    {
        private static readonly Logger logger = LogManager.GetLogger("RuntimeStatisticsGroup", LoggerType.Runtime);
        public long MemoryUsage => 0;
        public long TotalPhysicalMemory => int.MaxValue;
        public long AvailableMemory => TotalPhysicalMemory;
        public float CpuUsage => 0;

        internal void Start()
        {

            logger.Warn(ErrorCode.PerfCounterNotRegistered,
                "CPU & Memory perf counters are not available in .NET Standard. Load shedding will not work yet");
        }

        public void Stop()
        {
        }
    }
}
