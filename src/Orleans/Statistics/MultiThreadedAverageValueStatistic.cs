using System;

namespace Orleans.Runtime
{
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
}