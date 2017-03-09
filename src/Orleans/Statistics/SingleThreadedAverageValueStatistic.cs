namespace Orleans.Runtime
{
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