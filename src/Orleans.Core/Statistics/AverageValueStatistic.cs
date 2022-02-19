#define COLLECT_AVERAGE
using System;

namespace Orleans.Runtime
{
    internal class AverageValueStatistic
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

    internal class MultiThreadedAverageValueStatistic : AverageValueStatistic
    {
        private readonly CounterStatistic totalSum;
        private readonly CounterStatistic numItems;

        internal MultiThreadedAverageValueStatistic(StatisticName name)
            : base(name)
        {
            totalSum = CounterStatistic.FindOrCreate(new StatisticName(String.Format("{0}.{1}", name.Name, "TotalSum.Hidden")), false, CounterStorage.DontStore);
            numItems = CounterStatistic.FindOrCreate(new StatisticName(String.Format("{0}.{1}", name.Name, "NumItems.Hidden")), false, CounterStorage.DontStore);
        }

        public override void AddValue(long value)
        {
            totalSum.IncrementBy(value);
            numItems.Increment();
        }

        public override float GetAverageValue()
        {
            long nItems = this.numItems.GetCurrentValue();
            if (nItems == 0) return 0;

            long sum = this.totalSum.GetCurrentValue();
            return (float)sum / (float)nItems;
        }
    }

    // An optimized implementation to be used in a single threaded mode (not thread safe).
    internal class SingleThreadedAverageValueStatistic : AverageValueStatistic
    {
        private long totalSum;
        private long numItems;

        internal SingleThreadedAverageValueStatistic(StatisticName name)
            : base(name)
        {
            totalSum = 0;
            numItems = 0;
        }

        public override void AddValue(long value)
        {
            long oldTotal = totalSum;
            totalSum = (oldTotal + value);
            numItems = numItems + 1;
        }

        public override float GetAverageValue()
        {
            long nItems = this.numItems;
            if (nItems == 0) return 0;

            long sum = this.totalSum;
            return (float)sum / (float)nItems;
        }
    }
}
