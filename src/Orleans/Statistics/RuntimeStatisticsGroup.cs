/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

﻿#define LOG_MEMORY_PERF_COUNTERS
using System;
using System.Diagnostics;
using System.Management;

using System.Threading;
using System.Threading.Tasks;

namespace Orleans.Runtime
{
    internal class RuntimeStatisticsGroup
    {
        private static readonly TraceLogger logger = TraceLogger.GetLogger("RuntimeStatisticsGroup", TraceLogger.LoggerType.Runtime);

        private PerformanceCounter cpuCounter;
        private PerformanceCounter availableMemoryCounter;
#if LOG_MEMORY_PERF_COUNTERS
        private PerformanceCounter timeInGC;
        private PerformanceCounter[] genSizes;
        private PerformanceCounter allocatedBytesPerSec;
        private PerformanceCounter promotedMemoryFromGen1;
        private PerformanceCounter numberOfInducedGCs;
        private PerformanceCounter largeObjectHeapSize;
        private PerformanceCounter promotedFinalizationMemoryFromGen0;
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
        public long AvailableMemory { get { return availableMemoryCounter != null ? Convert.ToInt64(availableMemoryCounter.NextValue()) : 0; } }


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
                return String.Format("gen0={0:0.00}, gen1={1:0.00}, gen2={2:0.00}", genSizes[0].NextValue() / 1024f, genSizes[1].NextValue() / 1024f, genSizes[2].NextValue() / 1024f);
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
                cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total", true);
                availableMemoryCounter = new PerformanceCounter("Memory", "Available Bytes", true); 
#if LOG_MEMORY_PERF_COUNTERS
                string thisProcess = Process.GetCurrentProcess().ProcessName;
                timeInGC = new PerformanceCounter(".NET CLR Memory", "% Time in GC", thisProcess, true);
                genSizes = new PerformanceCounter[] { 
                    new PerformanceCounter(".NET CLR Memory", "Gen 0 heap size", thisProcess, true), 
                    new PerformanceCounter(".NET CLR Memory", "Gen 1 heap size", thisProcess, true), 
                    new PerformanceCounter(".NET CLR Memory", "Gen 2 heap size", thisProcess, true)
                };
                allocatedBytesPerSec = new PerformanceCounter(".NET CLR Memory", "Allocated Bytes/sec", thisProcess, true);
                promotedMemoryFromGen1 = new PerformanceCounter(".NET CLR Memory", "Promoted Memory from Gen 1", thisProcess, true);
                numberOfInducedGCs = new PerformanceCounter(".NET CLR Memory", "# Induced GC", thisProcess, true);
                largeObjectHeapSize = new PerformanceCounter(".NET CLR Memory", "Large Object Heap size", thisProcess, true);
                promotedFinalizationMemoryFromGen0 = new PerformanceCounter(".NET CLR Memory", "Promoted Finalization-Memory from Gen 0", thisProcess, true);
#endif

                // For Mono one could use PerformanceCounter("Mono Memory", "Total Physical Memory");
                const string Query = "SELECT Capacity FROM Win32_PhysicalMemory";
                var searcher = new ManagementObjectSearcher(Query);
                long Capacity = 0;
                foreach (ManagementObject WniPART in searcher.Get())
                    Capacity += Convert.ToInt64(WniPART.Properties["Capacity"].Value);

                if (Capacity == 0)
                    throw new Exception("No physical ram installed on machine?");

                TotalPhysicalMemory = Capacity;
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
                return;
            }

            cpuUsageTimer = new SafeTimer(CheckCpuUsage, null, CPU_CHECK_PERIOD, CPU_CHECK_PERIOD);
            try
            {
                // Read initial value of CPU Usage counter
                CpuUsage = cpuCounter.NextValue();
            }
            catch (InvalidOperationException)
            {
                // Can sometimes get exception accessing CPU Usage counter for first time in some runtime environments
                CpuUsage = 0;
            }

            FloatValueStatistic.FindOrCreate(StatisticNames.RUNTIME_CPUUSAGE, () => CpuUsage);
            IntValueStatistic.FindOrCreate(StatisticNames.RUNTIME_GC_TOTALMEMORYKB, () => (MemoryUsage + 1023) / 1024); // Round up
#if LOG_MEMORY_PERF_COUNTERS    // print GC stats in the silo log file.
            StringValueStatistic.FindOrCreate(StatisticNames.RUNTIME_GC_GENCOLLECTIONCOUNT, () => GCGenCollectionCount);
            StringValueStatistic.FindOrCreate(StatisticNames.RUNTIME_GC_GENSIZESKB, () => GCGenSizes);
            FloatValueStatistic.FindOrCreate(StatisticNames.RUNTIME_GC_PERCENTOFTIMEINGC, () => timeInGC.NextValue());
            FloatValueStatistic.FindOrCreate(StatisticNames.RUNTIME_GC_ALLOCATEDBYTESINKBPERSEC, () => allocatedBytesPerSec.NextValue() / 1024f);
            FloatValueStatistic.FindOrCreate(StatisticNames.RUNTIME_GC_PROMOTEDMEMORYFROMGEN1KB, () => promotedMemoryFromGen1.NextValue() / 1024f);
            FloatValueStatistic.FindOrCreate(StatisticNames.RUNTIME_GC_LARGEOBJECTHEAPSIZEKB, () => largeObjectHeapSize.NextValue() / 11024f);
            FloatValueStatistic.FindOrCreate(StatisticNames.RUNTIME_GC_PROMOTEDMEMORYFROMGEN0KB, () => promotedFinalizationMemoryFromGen0.NextValue() / 1024f);
            FloatValueStatistic.FindOrCreate(StatisticNames.RUNTIME_GC_NUMBEROFINDUCEDGCS, () => numberOfInducedGCs.NextValue());
            IntValueStatistic.FindOrCreate(StatisticNames.RUNTIME_MEMORY_TOTALPHYSICALMEMORYMB, () => (TotalPhysicalMemory / 1024) / 1024);
            if (availableMemoryCounter != null)
            {
                IntValueStatistic.FindOrCreate(StatisticNames.RUNTIME_MEMORY_AVAILABLEMEMORYMB, () => (AvailableMemory/ 1024) / 1024); // Round up
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
            var currentUsage = cpuCounter.NextValue();
            // We calculate a decaying average for CPU utilization
            CpuUsage = (CpuUsage + 2 * currentUsage) / 3;
        }

        public void Stop()
        {
            if (cpuUsageTimer != null)
                cpuUsageTimer.Dispose();
            cpuUsageTimer = null;
        }
    }
}