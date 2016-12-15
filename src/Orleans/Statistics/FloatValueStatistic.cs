
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Orleans.Runtime
{
    internal class FloatValueStatistic : ICounter<float>
    {
        private static readonly Dictionary<string, FloatValueStatistic> registeredStatistics;
        private static readonly object lockable;

        public string Name { get; }
        public CounterStorage Storage { get; private set; }

        private Func<float> fetcher;
        private Func<float, float> valueConverter;
        private readonly string currentName;

        static FloatValueStatistic()
        {
            registeredStatistics = new Dictionary<string, FloatValueStatistic>();
            lockable = new object();
        }

        // Must be called while Lockable is locked
        private FloatValueStatistic(string n, Func<float> f)
        {
            Name = n;
            currentName = Metric.CreateCurrentName(n);
            fetcher = f;
        }

        public static FloatValueStatistic Find(StatisticName name)
        {
            lock (lockable)
            {
                if (registeredStatistics.ContainsKey(name.Name))
                {
                    return registeredStatistics[name.Name];
                }
                else
                {
                    return null;
                }
            }
        }

        public static FloatValueStatistic FindOrCreate(StatisticName name, Func<float> f)
        {
            return FindOrCreate(name, f, CounterStorage.LogOnly);
        }

        public static FloatValueStatistic FindOrCreate(StatisticName name, Func<float> f, CounterStorage storage)
        {
            lock (lockable)
            {
                FloatValueStatistic stat;
                if (registeredStatistics.TryGetValue(name.Name, out stat))
                {
                    return stat;
                }
                var ctr = new FloatValueStatistic(name.Name, f) { Storage = storage };
                registeredStatistics[name.Name] = ctr;
                return ctr;
            }
        }

        public static void Delete(StatisticName name)
        {
            lock (lockable)
            {
                FloatValueStatistic stat;
                if (registeredStatistics.TryGetValue(name.Name, out stat))
                {
                    registeredStatistics.Remove(name.Name);
                    // Null the fetcher delegate to prevent memory leaks via undesirable reference capture by the fetcher lambda.
                    stat.fetcher = null;
                    stat.valueConverter = null;
                }
            }
        }

        public static FloatValueStatistic CreateDoNotRegister(string name, Func<float> f)
        {
            return new FloatValueStatistic(name, f) { Storage = CounterStorage.DontStore };
        }

        public FloatValueStatistic AddValueConverter(Func<float, float> converter)
        {
            this.valueConverter = converter;
            return this;
        }

        /// <summary>
        /// Returns the current value
        /// </summary>
        /// <returns></returns>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes")]
        public float GetCurrentValue()
        {
            float current = default(float);
            try
            {
                current = fetcher();
            }
            catch (Exception)
            {
            }

            if (valueConverter != null)
            {
                try
                {
                    current = valueConverter(current);
                }
                catch (Exception) { }
            }
            return current;
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
            float current = GetCurrentValue();
            return String.Format(CultureInfo.InvariantCulture, "{0:0.000}", current);
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
            return $"{Name}={GetValueString()}";
        }

        public void TrackMetric(Logger logger)
        {
            logger.TrackMetric(currentName, GetCurrentValue());
            // TODO: track delta, when we figure out how to calculate them accurately
        }
    }
}
