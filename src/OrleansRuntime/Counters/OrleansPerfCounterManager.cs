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

using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Orleans.Runtime.Counters
{
    internal static class OrleansPerfCounterManager
    {
        internal const string CATEGORY_NAME = "OrleansRuntime";
        internal const string CATEGORY_DESCRIPTION = "Orleans Runtime Counters";

        private static readonly TraceLogger logger = TraceLogger.GetLogger("OrleansPerfCounterManager", TraceLogger.LoggerType.Runtime);
        private static readonly List<PerfCounterConfigData> perfCounterData = new List<PerfCounterConfigData>();
        

        /// <summary>
        /// Have the perf counters we will use previously been registered with Windows? 
        /// </summary>
        /// <returns><c>true</c> if Windows perf counters are registered and available for Orleans</returns>
        public static bool AreWindowsPerfCountersAvailable()
        {
            try
            {
                return PerformanceCounterCategory.Exists(CATEGORY_NAME);
            }
            catch (Exception exc)
            {
                logger.Warn(ErrorCode.PerfCounterCategoryCheckError,
                    string.Format("Ignoring error checking for {0} perf counter category", CATEGORY_NAME), exc);
            }
            return false;
        }

        public static void PrecreateCounters()
        {
            GetCounterData();

            foreach (var cd in perfCounterData)
            {
                var perfCounterName = GetPerfCounterName(cd);
                cd.PerfCounter = CreatePerfCounter(perfCounterName);
            }
        }

        internal static void GetCounterData()
        {
            perfCounterData.Clear();

            // (1) Start with list of static counters
            perfCounterData.AddRange(PerfCounterConfigData.StaticPerfCounters);

            // (2) Then search for grain DLLs and pre-create activation counters for any grain types found
            var loadedGrainClasses = GrainTypeManager.Instance.GrainClassTypeData;
            foreach (var grainClass in loadedGrainClasses)
            {
                var counterName = new StatisticName(StatisticNames.GRAIN_COUNTS_PER_GRAIN, grainClass.Key);
                perfCounterData.Add(new PerfCounterConfigData
                {
                    Name = counterName,
                    UseDeltaValue = false,
                    CounterStat = CounterStatistic.FindOrCreate(counterName, false),
                });
            }
        }

        internal static CounterCreationData[] GetCounterCreationData()
        {
            GetCounterData();
            var ctrCreationData = new List<CounterCreationData>();
            foreach (PerfCounterConfigData cd in perfCounterData)
            {
                var perfCounterName = GetPerfCounterName(cd);
                var description = cd.Name.Name;

                var msg = string.Format("Registering perf counter {0}", perfCounterName);
                Console.WriteLine(msg);

                ctrCreationData.Add(new CounterCreationData(perfCounterName, description, PerformanceCounterType.NumberOfItems32));
            }
            return ctrCreationData.ToArray();
        }

        internal static PerformanceCounter CreatePerfCounter(string perfCounterName)
        {
            logger.Verbose(ErrorCode.PerfCounterRegistering, "Creating perf counter {0}", perfCounterName);
            return new PerformanceCounter(CATEGORY_NAME, perfCounterName, false);
        }

        /// <summary>
        /// Register Orleans perf counters with Windows
        /// </summary>
        /// <remarks>Note: Program needs to be running as Administrator to be able to delete Windows perf counters.</remarks>
        public static void InstallCounters()
        {
            var collection = new CounterCreationDataCollection();
            collection.AddRange(GetCounterCreationData());

            PerformanceCounterCategory.Create(
                CATEGORY_NAME,
                CATEGORY_DESCRIPTION,
                PerformanceCounterCategoryType.SingleInstance,
                collection);
        }

        /// <summary>
        /// Delete any existing perf counters registered with Windows
        /// </summary>
        /// <remarks>Note: Program needs to be running as Administrator to be able to delete Windows perf counters.</remarks>
        public static void DeleteCounters()
        {
            PerformanceCounterCategory.Delete(CATEGORY_NAME);
        }

        public static int WriteCounters()
        {
            if(logger.IsVerbose) logger.Verbose("Writing Windows perf counters.");

            int numWriteErrors = 0;

            foreach (PerfCounterConfigData cd in perfCounterData)
            {
                StatisticName name = cd.Name;
                string perfCounterName = GetPerfCounterName(cd);

                try
                {
                    if (cd.PerfCounter == null)
                    {
                        if (logger.IsVerbose) logger.Verbose(ErrorCode.PerfCounterUnableToConnect, "No perf counter found for {0}", name);
                        cd.PerfCounter = CreatePerfCounter(perfCounterName);
                    }

                    if (cd.CounterStat == null)
                    {
                        if (logger.IsVerbose) logger.Verbose(ErrorCode.PerfCounterRegistering, "Searching for statistic {0}", name);
                        ICounter<long> ctr = IntValueStatistic.Find(name);
                        cd.CounterStat = ctr ?? CounterStatistic.FindOrCreate(name);
                    }

                    long val;
                    //if (cd.UseDeltaValue)
                    //{
                    //    ((CounterStatistic)cd.CounterStat).GetCurrentValueAndDeltaAndResetDelta(out val);
                    //}
                    //else
                    {
                        val = cd.CounterStat.GetCurrentValue();
                    }
                    if (logger.IsVerbose3) logger.Verbose3(ErrorCode.PerfCounterWriting, "Writing perf counter {0} Value={1}", perfCounterName, val);
                    cd.PerfCounter.RawValue = val;
                }
                catch (Exception ex)
                {
                    numWriteErrors++;
                    logger.Error(ErrorCode.PerfCounterUnableToWrite, string.Format("Unable to write to Windows perf counter '{0}'", name), ex);
                }
            }
            return numWriteErrors;
        }

        private static string GetPerfCounterName(PerfCounterConfigData cd)
        {
            return cd.Name.Name + "." + (cd.UseDeltaValue ? "Delta" : "Current");
        }
    }
}
