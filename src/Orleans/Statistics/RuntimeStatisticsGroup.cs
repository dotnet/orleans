#define LOG_MEMORY_PERF_COUNTERS
using System;
using System.Diagnostics;
using System.Management;

using System.Threading;
using System.Threading.Tasks;

namespace Orleans.Runtime
{
    internal class RuntimeStatisticsGroup
    {
        private static readonly Logger logger = LogManager.GetLogger("RuntimeStatisticsGroup", LoggerType.Runtime);
        private const float KB = 1024f;

        private PerformanceCounter cpuCounterPF;
        private PerformanceCounter availableMemoryCounterPF;
#if LOG_MEMORY_PERF_COUNTERS
        private PerformanceCounter timeInGCPF;
        private PerformanceCounter[] genSizesPF;
        private PerformanceCounter allocatedBytesPerSecPF;
        private PerformanceCounter promotedMemoryFromGen1PF;
        private PerformanceCounter numberOfInducedGCsPF;
        private PerformanceCounter largeObjectHeapSizePF;
        private PerformanceCounter promotedFinalizationMemoryFromGen0PF;
#endif
        private SafeTimer cpuUsageTimer;
        private readonly TimeSpan CPU_CHECK_PERIOD = TimeSpan.FromSeconds(5);
        private readonly TimeSpan INITIALIZATION_TIMEOUT = TimeSpan.FromMinutes(1);
        private bool countersAvailable;


        public long MemoryUsage { get { return GC.GetTotalMemory(false); } }

        ///
        /// <summary>Amount of physical memory on the machine</summary>
        /// 
        public long TotalPhysicalMemory  { get; private set; }

        ///
        /// <summary>Amount of memory available to processes running on the machine</summary>
        /// 
        public long AvailableMemory { get { return availableMemoryCounterPF != null ? Convert.ToInt64(availableMemoryCounterPF.NextValue()) : 0; } }

        public float CpuUsage { get; private set; }

        private static string GCGenCollectionCount
        {
            get
            {
                return String.Format("gen0={0}, gen1={1}, gen2={2}", GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));
            }
        }

#if LOG_MEMORY_PERF_COUNTERS
        private string GCGenSizes
        {
            get
            {
                if (genSizesPF == null) return String.Empty;
                return String.Format("gen0={0:0.00}, gen1={1:0.00}, gen2={2:0.00}", genSizesPF[0].NextValue() / KB, genSizesPF[1].NextValue() / KB, genSizesPF[2].NextValue() / KB);
            }
        }
#endif
        internal RuntimeStatisticsGroup()
        {
            try
            {
                Task.Run(() =>
                {
                    InitCpuMemoryCounters();
                }).WaitWithThrow(INITIALIZATION_TIMEOUT);  
            }
            catch (TimeoutException)
            {
                logger.Warn(ErrorCode.PerfCounterConnectError,
                    "Timeout occurred during initialization of CPU & Memory perf counters");
            }          
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes")]
        private void InitCpuMemoryCounters()
        {
            try
            {
                cpuCounterPF = new PerformanceCounter("Processor", "% Processor Time", "_Total", true);
                availableMemoryCounterPF = new PerformanceCounter("Memory", "Available Bytes", true); 
#if LOG_MEMORY_PERF_COUNTERS
                string thisProcess = Process.GetCurrentProcess().ProcessName;
                timeInGCPF = new PerformanceCounter(".NET CLR Memory", "% Time in GC", thisProcess, true);
                genSizesPF = new PerformanceCounter[] { 
                    new PerformanceCounter(".NET CLR Memory", "Gen 0 heap size", thisProcess, true), 
                    new PerformanceCounter(".NET CLR Memory", "Gen 1 heap size", thisProcess, true), 
                    new PerformanceCounter(".NET CLR Memory", "Gen 2 heap size", thisProcess, true)
                };
                allocatedBytesPerSecPF = new PerformanceCounter(".NET CLR Memory", "Allocated Bytes/sec", thisProcess, true);
                promotedMemoryFromGen1PF = new PerformanceCounter(".NET CLR Memory", "Promoted Memory from Gen 1", thisProcess, true);
                numberOfInducedGCsPF = new PerformanceCounter(".NET CLR Memory", "# Induced GC", thisProcess, true);
                largeObjectHeapSizePF = new PerformanceCounter(".NET CLR Memory", "Large Object Heap size", thisProcess, true);
                promotedFinalizationMemoryFromGen0PF = new PerformanceCounter(".NET CLR Memory", "Promoted Finalization-Memory from Gen 0", thisProcess, true);
#endif
                
#if !(NETSTANDARD || __MonoCS__)
                //.NET on Windows without mono
                const string Query = "SELECT Capacity FROM Win32_PhysicalMemory";
                var searcher = new ManagementObjectSearcher(Query);
                long Capacity = 0;
                foreach (ManagementObject WniPART in searcher.Get())
                    Capacity += Convert.ToInt64(WniPART.Properties["Capacity"].Value);

                if (Capacity == 0)
                    throw new Exception("No physical ram installed on machine?");

                TotalPhysicalMemory = Capacity;
#elif __MonoCS__
                //Cross platform mono
                var totalPhysicalMemory = new PerformanceCounter("Mono Memory", "Total Physical Memory");
                TotalPhysicalMemory = Convert.ToInt64(totalPhysicalMemory.NextValue());
#elif NETSTANDARD
                //Cross platform CoreCLR
#endif
                countersAvailable = true;
            }
            catch (Exception)
            {
                logger.Warn(ErrorCode.PerfCounterConnectError,
                    "Error initializing CPU & Memory perf counters - you need to repair Windows perf counter config on this machine with 'lodctr /r' command");
            }
        }

        internal void Start()
        {
            if (!countersAvailable)
            {
                logger.Warn(ErrorCode.PerfCounterNotRegistered,
                    "CPU & Memory perf counters did not initialize correctly - try repairing Windows perf counter config on this machine with 'lodctr /r' command");
            }

            if (cpuCounterPF != null) {
                cpuUsageTimer = new SafeTimer(CheckCpuUsage, null, CPU_CHECK_PERIOD, CPU_CHECK_PERIOD);
            }
            try
            {
                if (cpuCounterPF != null)
                {
                    // Read initial value of CPU Usage counter
                    CpuUsage = cpuCounterPF.NextValue();
                }
            }
            catch (InvalidOperationException)
            {
                // Can sometimes get exception accessing CPU Usage counter for first time in some runtime environments
                CpuUsage = 0;
            }

            FloatValueStatistic.FindOrCreate(StatisticNames.RUNTIME_CPUUSAGE, () => CpuUsage);
            IntValueStatistic.FindOrCreate(StatisticNames.RUNTIME_GC_TOTALMEMORYKB, () => (long)((MemoryUsage + KB-1.0) / KB)); // Round up
#if LOG_MEMORY_PERF_COUNTERS    // print GC stats in the silo log file.
            StringValueStatistic.FindOrCreate(StatisticNames.RUNTIME_GC_GENCOLLECTIONCOUNT, () => GCGenCollectionCount);
            StringValueStatistic.FindOrCreate(StatisticNames.RUNTIME_GC_GENSIZESKB, () => GCGenSizes);
            if (timeInGCPF != null)
            {
                FloatValueStatistic.FindOrCreate(StatisticNames.RUNTIME_GC_PERCENTOFTIMEINGC, () => timeInGCPF.NextValue());
            }
            if (allocatedBytesPerSecPF != null)
            {
                FloatValueStatistic.FindOrCreate(StatisticNames.RUNTIME_GC_ALLOCATEDBYTESINKBPERSEC, () => allocatedBytesPerSecPF.NextValue() / KB);
            }
            if (promotedMemoryFromGen1PF != null)
            {
                FloatValueStatistic.FindOrCreate(StatisticNames.RUNTIME_GC_PROMOTEDMEMORYFROMGEN1KB, () => promotedMemoryFromGen1PF.NextValue() / KB);
            }
            if (largeObjectHeapSizePF != null)
            {
                FloatValueStatistic.FindOrCreate(StatisticNames.RUNTIME_GC_LARGEOBJECTHEAPSIZEKB, () => largeObjectHeapSizePF.NextValue() / KB);
            }
            if (promotedFinalizationMemoryFromGen0PF != null)
            {
                FloatValueStatistic.FindOrCreate(StatisticNames.RUNTIME_GC_PROMOTEDMEMORYFROMGEN0KB, () => promotedFinalizationMemoryFromGen0PF.NextValue() / KB);
            }
            if (numberOfInducedGCsPF != null)
            {
                FloatValueStatistic.FindOrCreate(StatisticNames.RUNTIME_GC_NUMBEROFINDUCEDGCS, () => numberOfInducedGCsPF.NextValue());
            }
            IntValueStatistic.FindOrCreate(StatisticNames.RUNTIME_MEMORY_TOTALPHYSICALMEMORYMB, () => (long)((TotalPhysicalMemory / KB) / KB));
            if (availableMemoryCounterPF != null)
            {
                IntValueStatistic.FindOrCreate(StatisticNames.RUNTIME_MEMORY_AVAILABLEMEMORYMB, () => (long)((AvailableMemory/ KB) / KB)); // Round up
            }
#endif
            IntValueStatistic.FindOrCreate(StatisticNames.RUNTIME_DOT_NET_THREADPOOL_INUSE_WORKERTHREADS, () =>
            {
                int maXworkerThreads;
                int maXcompletionPortThreads;
                ThreadPool.GetMaxThreads(out maXworkerThreads, out maXcompletionPortThreads);
                int workerThreads;
                int completionPortThreads;
                // GetAvailableThreads Retrieves the difference between the maximum number of thread pool threads
                // and the number currently active.
                // So max-Available is the actual number in use. If it goes beyond min, it means we are stressing the thread pool.
                ThreadPool.GetAvailableThreads(out workerThreads, out completionPortThreads);
                return maXworkerThreads - workerThreads;
            });
            IntValueStatistic.FindOrCreate(StatisticNames.RUNTIME_DOT_NET_THREADPOOL_INUSE_COMPLETIONPORTTHREADS, () =>
            {
                int maXworkerThreads;
                int maXcompletionPortThreads;
                ThreadPool.GetMaxThreads(out maXworkerThreads, out maXcompletionPortThreads);
                int workerThreads;
                int completionPortThreads;
                ThreadPool.GetAvailableThreads(out workerThreads, out completionPortThreads);
                return maXcompletionPortThreads - completionPortThreads;
            });
        }

        private void CheckCpuUsage(object m)
        {
            if (cpuCounterPF != null)
            {
                var currentUsage = cpuCounterPF.NextValue();
                // We calculate a decaying average for CPU utilization
                CpuUsage = (CpuUsage + 2 * currentUsage) / 3;
            }else
            {
                CpuUsage = 0;
            }
        }

        public void Stop()
        {
            if (cpuUsageTimer != null)
                cpuUsageTimer.Dispose();
            cpuUsageTimer = null;
        }
    }
}
