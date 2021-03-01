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
        void TrackMetric(ITelemetryProducer telemetryProducer);
    }

    public static class Metric
    {
        public static string CreateCurrentName(string statisticName)
        {
            return statisticName + "." + "Current";
        }
    }

    internal interface ICounter<out T> : ICounter
    {
        T GetCurrentValue();
    }

    internal class CounterStatistic : ICounter<long>
    {
        private static readonly ConcurrentDictionary<string, CounterStatistic> registeredStatistics;
        private static readonly object lockable;
        
        private long last;
        private bool firstStatDisplay;
        private Func<long, long> valueConverter;
        private long value;
        private readonly bool isHidden;

        public string Name { get; }
        public bool UseDelta { get; }
        public CounterStorage Storage { get; }

        static CounterStatistic()
        {
            registeredStatistics = new ConcurrentDictionary<string, CounterStatistic>();           
            lockable = new object();
        }

        // Must be called while Lockable is locked
        private CounterStatistic(string name, bool useDelta, CounterStorage storage, bool isHidden)
        {
            Name = name;
            UseDelta = useDelta;
            Storage = storage;
            last = 0;
            firstStatDisplay = true;
            valueConverter = null;
            value = 0;
            this.isHidden = isHidden;
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
            if (registeredStatistics.TryGetValue(name.Name, out var stat))
            {
                return stat;
            }

            return registeredStatistics.GetOrAdd(name.Name, new CounterStatistic(name.Name, useDelta, storage, isHidden));
        }

        public static bool Delete(string name)
        {
            return registeredStatistics.Remove(name, out _);
        }

        public CounterStatistic AddValueConverter(Func<long, long> converter)
        {
            this.valueConverter = converter;
            return this;
        }

        public void Increment()
        {
            Interlocked.Increment(ref value);
        }

        public void DecrementBy(long n)
        {
            Interlocked.Add(ref value, -n);
        }

        public void IncrementBy(long n)
        {
            Interlocked.Add(ref value, n);
        }

        /// <summary>
        /// Returns the current value
        /// </summary>
        /// <returns></returns>
        public long GetCurrentValue()
        {            
            return Volatile.Read(ref value);            
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
            list.AddRange(registeredStatistics.Select(c => c.Value).Where(c => !c.isHidden && predicate(c)));
        }

        public void TrackMetric(ITelemetryProducer telemetryProducer)
        {
            var rawValue = GetCurrentValue();
            var value = valueConverter?.Invoke(rawValue) ?? rawValue;
            telemetryProducer.TrackMetric(Name, value);
            // TODO: track delta, when we figure out how to calculate them accurately
        }
    }
}
