#define COLLECT_AVERAGE
using System;

namespace Orleans.Runtime
{
    class AverageValueStatistic
    {
#if COLLECT_AVERAGE
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Performance", "CA1823:AvoidUnusedPrivateFields")]
        private FloatValueStatistic average;
#endif
        public string Name { get; private set; }

        static public AverageValueStatistic FindOrCreate(StatisticName name, CounterStorage storage = CounterStorage.LogOnly)
        {
            return FindOrCreate_Impl(name, storage, true);
        }

        static private AverageValueStatistic FindOrCreate_Impl(StatisticName name, CounterStorage storage, bool multiThreaded)
        {
            AverageValueStatistic stat;
#if COLLECT_AVERAGE
            if (multiThreaded)
            {
                stat = new MultiThreadedAverageValueStatistic(name);
            }
            else
            {
                stat = new SingleThreadedAverageValueStatistic(name);
            }
            stat.average = FloatValueStatistic.FindOrCreate(name,
                      () => stat.GetAverageValue(), storage);
#else
            stat = new AverageValueStatistic(name);
#endif
            
            return stat;
        }

        static public void Delete(StatisticName name)
        {
            FloatValueStatistic.Delete(name);
        }

        protected AverageValueStatistic(StatisticName name)
        {
            Name = name.Name;
        }

        public virtual void AddValue(long value) { }

        public virtual float GetAverageValue() { return 0; }

        public override string ToString()
        {
            return Name;
        }

        public AverageValueStatistic AddValueConverter(Func<float, float> converter)
        {
            this.average.AddValueConverter(converter);
            return this;
        }
    }

    // An optimized implementation to be used in a single threaded mode (not thread safe).
}
