
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Orleans.Runtime
{
    class IntValueStatistic : ICounter<long>
    {
        private static readonly Dictionary<string, IntValueStatistic> registeredStatistics;
        private static readonly object lockable;
        private readonly string currentName;

        public string Name { get; }
        public CounterStorage Storage { get; private set; }

        private Func<long> fetcher;

        static IntValueStatistic()
        {
            registeredStatistics = new Dictionary<string, IntValueStatistic>();
            lockable = new object();
        }

        // Must be called while Lockable is locked
        private IntValueStatistic(string n, Func<long> f)
        {
            Name = n;
            currentName = Metric.CreateCurrentName(n);
            fetcher = f;
        }

        public static IntValueStatistic Find(StatisticName name)
        {
            lock (lockable)
            {
                return registeredStatistics.ContainsKey(name.Name) ? registeredStatistics[name.Name] : null;
            }
        }

        public static IntValueStatistic FindOrCreate(StatisticName name, Func<long> f, CounterStorage storage = CounterStorage.LogOnly)
        {
            lock (lockable)
            {
                IntValueStatistic stat;
                if (registeredStatistics.TryGetValue(name.Name, out stat))
                {
                    return stat;
                }
                var ctr = new IntValueStatistic(name.Name, f) { Storage = storage };
                registeredStatistics[name.Name] = ctr;
                return ctr;
            }
        }

        public static void Delete(StatisticName name)
        {
            lock (lockable)
            {
                IntValueStatistic stat;
                if (registeredStatistics.TryGetValue(name.Name, out stat))
                {
                    registeredStatistics.Remove(name.Name);
                    // Null the fetcher delegate to prevent memory leaks via undesirable reference capture by the fetcher lambda.
                    stat.fetcher = null;
                }
            }   
        }

        /// <summary>
        /// Returns the current value
        /// </summary>
        /// <returns></returns>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes")]
        public long GetCurrentValue()
        {
            try
            {
                return fetcher();
            }
            catch (Exception)
            {
                return default(long);
            }
        }

        public static void AddCounters(List<ICounter> list, Func<ICounter, bool> predicate)
        {
            lock (lockable)
            {
                list.AddRange(registeredStatistics.Values.Where(predicate));
            }
        }

        public bool IsValueDelta => false;

        public string GetValueString()
        {
            long current = GetCurrentValue();
            return current.ToString(CultureInfo.InvariantCulture);
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
            return ToString();
        }

        public override string ToString()
        {
            return Name + "=" + GetValueString();
        }

        public void TrackMetric(Logger logger)
        {
            logger.TrackMetric(currentName, GetCurrentValue());
            // TODO: track delta, when we figure out how to calculate them accurately
        }
    }
}
