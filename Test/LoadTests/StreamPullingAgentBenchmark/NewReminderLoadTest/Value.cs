namespace StreamPullingAgentBenchmark.NewReminderLoadTest
{
    public class Value
    {
        private long _total;
        private long _totalCount;

        private long _delta;
        private long _deltaCount;

        public long Total { get { return _total + _delta; } }
        public long TotalCount { get { return _totalCount; } }

        public double TotalAverage
        {
            get
            {
                return (double)_total / _totalCount;
            }
        }

        public long Delta { get { return _delta; } }
        public long DeltaCount { get { return _deltaCount; } }

        public double DeltaAverage
        {
            get
            {
                return (double)_delta / _deltaCount;
            }
        }

        public void Increment(long delta)
        {
            _delta += delta;
            _deltaCount++;
        }

        public void Flush()
        {
            _total += _delta;
            _totalCount += _deltaCount;
            _delta = 0;
            _deltaCount = 0;
        }
    }
}