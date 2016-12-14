using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;

namespace Orleans.Runtime
{
    public enum CounterStorage
    {
        DontStore,
        LogOnly,
        LogAndTable
    }

    /// <summary>
    /// A detailed statistic counter. Usually a low level performance statistic used in troubleshooting scenarios.
    /// </summary>
    public interface ICounter
    {
        /// <summary>
        /// the name of the statistic counter
        /// </summary>
        string Name { get; }

        /// <summary>
        /// if this the statistic counter value is delta since last value reported or an absolute value
        /// </summary>
        bool IsValueDelta { get; }
        string GetValueString();
        string GetDeltaString();
        void ResetCurrent();
        string GetDisplayString();
        CounterStorage Storage { get; }
        void TrackMetric(Logger logger);
    }

    public static class Metric
    {
        public static string CreateCurrentName(string statisticName)
        {
            return statisticName + "." + "Current";
        }
        public static string CreateDeltaName(string statisticName)
        {
            return statisticName + "." + "Delta";
        }
    }

    internal interface ICounter<out T> : ICounter
    {
        T GetCurrentValue();
    }

    internal class CounterStatistic : ICounter<long>
    {
        private class ReferenceLong
        {
            internal long Value;
        }

        private const int BUFFER_SIZE = 100;

        [ThreadStatic]
        private static List<ReferenceLong> allStatisticsFromSpecificThread;
        [ThreadStatic]
        private static bool isOrleansManagedThread;

        private static readonly Dictionary<string, CounterStatistic> registeredStatistics;
        private static readonly object lockable;
        private static int nextId;
        private readonly ConcurrentStack<ReferenceLong> specificStatisticFromAllThreads;
        
        private readonly int id;
        private long last;
        private bool firstStatDisplay;
        private Func<long, long> valueConverter;
        private long nonOrleansThreadsCounter; // one for all non-Orleans threads
        private readonly bool isHidden;
        private readonly string currentName;

        public string Name { get; }
        public bool UseDelta { get; }
        public CounterStorage Storage { get; }

        static CounterStatistic()
        {
            registeredStatistics = new Dictionary<string, CounterStatistic>();           
            nextId = 0;
            lockable = new object();
        }

        // Must be called while Lockable is locked
        private CounterStatistic(string name, bool useDelta, CounterStorage storage, bool isHidden)
        {
            Name = name;
            currentName = Metric.CreateCurrentName(name);
            UseDelta = useDelta;
            Storage = storage;
            id = Interlocked.Increment(ref nextId);
            last = 0;
            firstStatDisplay = true;
            valueConverter = null;
            nonOrleansThreadsCounter = 0;
            this.isHidden = isHidden;
            specificStatisticFromAllThreads = new ConcurrentStack<ReferenceLong>();
        }

        internal static void SetOrleansManagedThread()
        {
            if (!isOrleansManagedThread)
            {
                lock (lockable)
                {
                    isOrleansManagedThread = true;
                    allStatisticsFromSpecificThread = new List<ReferenceLong>(new ReferenceLong[BUFFER_SIZE]);                    
                }
            }
        }

        public static CounterStatistic FindOrCreate(StatisticName name)
        {
            return FindOrCreate_Impl(name, true, CounterStorage.LogAndTable, false);
        }

        public static CounterStatistic FindOrCreate(StatisticName name, bool useDelta, bool isHidden = false)
        {
            return FindOrCreate_Impl(name, useDelta, CounterStorage.LogAndTable, isHidden);
        }

        public static CounterStatistic FindOrCreate(StatisticName name, CounterStorage storage, bool isHidden = false)
        {
            return FindOrCreate_Impl(name, true, storage, isHidden);
        }

        public static CounterStatistic FindOrCreate(StatisticName name, bool useDelta, CounterStorage storage, bool isHidden = false)
        {
            return FindOrCreate_Impl(name, useDelta, storage, isHidden);
        }

        private static CounterStatistic FindOrCreate_Impl(StatisticName name, bool useDelta, CounterStorage storage, bool isHidden)
        {
            lock (lockable)
            {
                CounterStatistic stat;
                if (registeredStatistics.TryGetValue(name.Name, out stat))
                {
                    return stat;
                }
                var ctr = new CounterStatistic(name.Name, useDelta, storage, isHidden);

                registeredStatistics[name.Name] = ctr;

                return ctr;
            }
        }

        public static bool Delete(string name)
        {
            lock (lockable)
            {
                return registeredStatistics.Remove(name);
            }
        }

        public CounterStatistic AddValueConverter(Func<long, long> converter)
        {
            this.valueConverter = converter;
            return this;
        }

        public void Increment()
        {
            IncrementBy(1);
        }

        public void DecrementBy(long n)
        {
            IncrementBy(-n);
        }

        // Orleans-managed threads aggregate stats in per thread local storage list.
        // For non Orleans-managed threads (.NET IO completion port threads, thread pool timer threads) we don't want to allocate a thread local storage,
        // since we don't control how many of those threads are created (could lead to too much thread local storage allocated).
        // Thus, for non Orleans-managed threads, we use a counter shared between all those threads and Interlocked.Add it (creating small contention).
        public void IncrementBy(long n)
        {
            if (isOrleansManagedThread)
            {
                if (allStatisticsFromSpecificThread.Count <= id)
                {
                    var bufferSize = Math.Max(id - allStatisticsFromSpecificThread.Count, BUFFER_SIZE);

                    allStatisticsFromSpecificThread.AddRange(new ReferenceLong[bufferSize+1]);
                }

                var orleansValue = allStatisticsFromSpecificThread[id];

                if (orleansValue == null)
                {
                    orleansValue = new ReferenceLong();
                    allStatisticsFromSpecificThread[id] = orleansValue;
                    specificStatisticFromAllThreads.Push(orleansValue);
                }

                orleansValue.Value += n;
            }
            else
            {
                if (n == 1)
                {
                    Interlocked.Increment(ref nonOrleansThreadsCounter);
                }
                else
                {
                    Interlocked.Add(ref nonOrleansThreadsCounter, n);
                }
            }
        }

        /// <summary>
        /// Returns the current value
        /// </summary>
        /// <returns></returns>
        public long GetCurrentValue()
        {            
            long val = specificStatisticFromAllThreads.Sum(a => a.Value);         
            return val + Interlocked.Read(ref nonOrleansThreadsCounter);            
        }


        // does not reset delta
        public long GetCurrentValueAndDelta(out long delta)
        {
            long currentValue = GetCurrentValue();
            delta = UseDelta ? (currentValue - last) : 0;
            return currentValue;
        }

        public bool IsValueDelta => UseDelta;

        public void ResetCurrent()
        {
            var currentValue = GetCurrentValue();
            last = currentValue;
        }

        public string GetValueString()
        {
            long current = GetCurrentValue();

            if (valueConverter != null)
            {
                try
                {
                    current = valueConverter(current);
                }
                catch (Exception) { }
            }

            return current.ToString(CultureInfo.InvariantCulture);
        }

        public string GetDeltaString()
        {
            long current = GetCurrentValue();
            long delta = UseDelta ? (current - last) : 0;

            if (valueConverter != null)
            {
                try
                {
                    delta = valueConverter(delta);
                }
                catch (Exception) { }
            }

            return delta.ToString(CultureInfo.InvariantCulture);
        }

        public string GetDisplayString()
        {
            long delta;
            
            long current = GetCurrentValueAndDelta(out delta);

            if (firstStatDisplay)
            {
                delta = 0; // Special case: don't output first delta
                firstStatDisplay = false;
            }

            if (valueConverter != null)
            {
                try
                {
                    current = valueConverter(current);
                }
                catch (Exception) { }
                try
                {
                    delta = valueConverter(delta);
                }
                catch (Exception) { }
            }

            if (delta == 0)
            {
                return $"{Name}.Current={current.ToString(CultureInfo.InvariantCulture)}";
            }
            else
            {
                return
                    $"{Name}.Current={current.ToString(CultureInfo.InvariantCulture)},      Delta={delta.ToString(CultureInfo.InvariantCulture)}";
            }
        }

        public override string ToString()
        {
            return GetDisplayString();
        }

        public static void AddCounters(List<ICounter> list, Func<CounterStatistic, bool> predicate)
        {
            lock (lockable)
            {
                list.AddRange(registeredStatistics.Values.Where( c => !c.isHidden && predicate(c)));
            }
        }

        public void TrackMetric(Logger logger)
        {
            logger.TrackMetric(currentName, GetCurrentValue());
            // TODO: track delta, when we figure out how to calculate them accurately
        }
    }
}
