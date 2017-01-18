
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Orleans.Runtime
{
    internal class AverageTimeSpanStatistic : ICounter<TimeSpan>
    {
        private static readonly Dictionary<string, AverageTimeSpanStatistic> registeredStatistics;
        private static readonly object classLock;

        private readonly object instanceLock;
        private readonly CounterStatistic tickAccum;
        private readonly CounterStatistic sampleCounter;
        private readonly string currentName;

        public string Name { get; }
        public bool IsValueDelta { get; }
        public CounterStorage Storage { get; }

        static AverageTimeSpanStatistic()
        {
            registeredStatistics = new Dictionary<string, AverageTimeSpanStatistic>();
            classLock = new object();
        }

        private AverageTimeSpanStatistic(string name, CounterStorage storage)
        {
            Name = name;
            currentName = Metric.CreateCurrentName(name);
            IsValueDelta = false;
            Storage = storage;
            // the following counters are used internally and shouldn't be stored. the derived value,
            // the average timespan, will be stored in 'storage'.
            tickAccum = CounterStatistic.FindOrCreate(new StatisticName(name + ".tickAccum"), IsValueDelta, CounterStorage.DontStore);
            sampleCounter = CounterStatistic.FindOrCreate(new StatisticName(name + ".sampleCounter"), IsValueDelta, CounterStorage.DontStore);
            instanceLock = new object();
        }

        public static bool TryFind(StatisticName name, out AverageTimeSpanStatistic result)
        {
            lock (classLock)
            {
                return registeredStatistics.TryGetValue(name.Name, out result);
            }
        }

        private static void 
            ThrowIfNotConsistent(
                AverageTimeSpanStatistic expected, 
                StatisticName name,  
                CounterStorage storage)
        {
            if (storage != expected.Storage)
            {
                throw new ArgumentException(
                    $"Please verity that all invocations of AverageTimeSpanStatistic.FindOrCreate() for instance \"{name.Name}\" all specify the same storage type {Enum.GetName(typeof(CounterStorage), expected.Storage)}",
                    nameof(storage)); 
            }
        }

        public static AverageTimeSpanStatistic 
            FindOrCreate(
                StatisticName name, 
                CounterStorage storage = CounterStorage.LogOnly)
        {
            lock (classLock)
            {
                AverageTimeSpanStatistic result;
                if (TryFind(name, out result))
                {
                    ThrowIfNotConsistent(result, name, storage);
                    return result;
                }
                else
                {
                    var newOb = new AverageTimeSpanStatistic(name.Name, storage);
                    registeredStatistics[name.Name] = newOb;
                    return newOb;
                }
            }
        }

        public static void Delete(StatisticName name)
        {
            lock (classLock)
            {
                AverageTimeSpanStatistic stat;
                if (registeredStatistics.TryGetValue(name.Name, out stat))
                {
                    registeredStatistics.Remove(name.Name);
                }
            }
        }

        public static void AddCounters(List<ICounter> list, Func<ICounter, bool> predicate)
        {
            lock (classLock)
            {
                list.AddRange(registeredStatistics.Values.Where(predicate));
            }
        }

        public void AddSample(long tickCount)
        {
            lock (instanceLock)
            {
                tickAccum.IncrementBy(tickCount);
                sampleCounter.Increment();
            }
        }

        public void AddSample(TimeSpan dt)
        {
            AddSample(dt.Ticks);
        }

        public TimeSpan GetCurrentValue()
        {
            long sampleCount, tickCount;
            lock (instanceLock)
            {
                sampleCount = sampleCounter.GetCurrentValue();
                tickCount = tickAccum.GetCurrentValue();
            }
            return sampleCount == 0 ? TimeSpan.Zero : TimeSpan.FromTicks(tickCount / sampleCount);
        }

        public string GetValueString()
        {
            return GetCurrentValue().TotalSeconds.ToString(CultureInfo.InvariantCulture);
        }

        public string GetDeltaString()
        {
            throw new NotSupportedException();
        }

        public void ResetCurrent()
        {
            throw new NotSupportedException();
        }

        public string GetDisplayString()
        {
            return $"{Name}={GetValueString()} Secs";
        }
        
        public override string ToString()
        {
            return GetValueString();
        }

        public void TrackMetric(Logger logger)
        {
            logger.TrackMetric(currentName, GetCurrentValue());
            // TODO: track delta, when we figure out how to calculate them accurately
        }
    }
}
