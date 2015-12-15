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

        public string Name { get; private set; }
        public CounterStorage Storage { get; private set; }

        private readonly Func<float> fetcher;
        private Func<float, float> valueConverter;

        static FloatValueStatistic()
        {
            registeredStatistics = new Dictionary<string, FloatValueStatistic>();
            lockable = new object();
        }

        // Must be called while Lockable is locked
        private FloatValueStatistic(string n, Func<float> f)
        {
            Name = n;
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

        static public FloatValueStatistic FindOrCreate(StatisticName name, Func<float> f)
        {
            return FindOrCreate(name, f, CounterStorage.LogOnly);
        }

        static public FloatValueStatistic FindOrCreate(StatisticName name, Func<float> f, CounterStorage storage)
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

        static public FloatValueStatistic CreateDoNotRegister(string name, Func<float> f)
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

        public bool IsValueDelta { get { return false; } }

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
            return String.Format("{0}={1}", Name, GetValueString());
        }
    }
}
