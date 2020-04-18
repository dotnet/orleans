
using System;
using System.Collections.Generic;
using System.Linq;

namespace Orleans.Runtime
{
    class StringValueStatistic : ICounter<string>
    {
        private static readonly Dictionary<string, StringValueStatistic> dict = new Dictionary<string, StringValueStatistic>();

        public string Name { get; }
        public CounterStorage Storage { get; private set; }

        private Func<string> fetcher;

        // Must be called while Lockable is locked
        private StringValueStatistic(string n, Func<string> f)
        {
            Name = n;
            fetcher = f;
        }

        public static StringValueStatistic Find(StatisticName name)
        {
            lock (dict)
            {
                dict.TryGetValue(name.Name, out var stat);
                return stat;
            }
        }

        public static StringValueStatistic FindOrCreate(StatisticName name, Func<string> f, CounterStorage storage = CounterStorage.LogOnly)
        {
            lock (dict)
            {
                if (dict.TryGetValue(name.Name, out var stat))
                {
                    return stat;
                }
                var ctr = new StringValueStatistic(name.Name, f) { Storage = storage };
                dict[name.Name] = ctr;
                return ctr;
            }
        }

        public static void Delete(string name)
        {
            lock (dict)
            {
                if (dict.TryGetValue(name, out var stat))
                {
                    dict.Remove(name);
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
        public string GetCurrentValue()
        {
            try
            {
                return fetcher();
            }
            catch (Exception)
            {
                return "";
            }
        }

        public static void AddCounters(List<ICounter> list, Func<ICounter, bool> predicate)
        {
            lock (dict)
            {
                list.AddRange(dict.Values.Where(predicate));
            }
        }

        public bool IsValueDelta => false;

        public string GetValueString()
        {
            return GetCurrentValue();
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
            return Name + "=" + GetCurrentValue();
        }

        public void TrackMetric(ITelemetryProducer telemetryProducer)
        {
            // String values are not tracked.
        }
    }
}
