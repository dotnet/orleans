using System;
using System.Diagnostics;

namespace Orleans.SqlUtils.StorageProvider.Instrumentation
{
    public class WritablePerformanceCounter
    {
        public string CategoryName { get; private set; }
        public string CounterName { get; private set; }
        public string InstanceName { get; private set; }

        private readonly PerformanceCounter _performanceCounter;

        public WritablePerformanceCounter(string categoryName, string counterName)
        {
            CategoryName = categoryName;
            CounterName = counterName;
            InstanceName = null;

            try
            {
                // Create the performance counter.
                PerformanceCounter performanceCounter = new PerformanceCounter(CategoryName, CounterName, false);
                _performanceCounter = performanceCounter;
            }
            catch (Exception)
            {
                Console.WriteLine("Failed to create counter {0}/{1}", categoryName, counterName);
            }
        }

        public WritablePerformanceCounter(string categoryName, string counterName, string instanceName)
        {
            CategoryName = categoryName;
            CounterName = counterName;
            InstanceName = instanceName;

            try
            {
                // Create the performance counter.
                PerformanceCounter performanceCounter = new PerformanceCounter(CategoryName, CounterName, InstanceName, false);
                _performanceCounter = performanceCounter;
            }
            catch (Exception)
            {
            }
        }

        public void SetCurrentTimestamp()
        {
            if (null != _performanceCounter)
                _performanceCounter.RawValue = Stopwatch.GetTimestamp();
        }

        public long Increment()
        {
            if (null != _performanceCounter)
                return _performanceCounter.Increment();
            return 0;
        }

        public long Decrement()
        {
            if (null != _performanceCounter)
                return _performanceCounter.Decrement();
            return 0;
        }

        public long DecrementBy(long value)
        {
            if (null != _performanceCounter)
                return _performanceCounter.IncrementBy(-value);
            return 0;
        }

        public long IncrementBy(long value)
        {
            if (null != _performanceCounter)
                return _performanceCounter.IncrementBy(value);
            return 0;
        }

        public void ResetCounter()
        {
            if (null != _performanceCounter)
                _performanceCounter.RawValue = 0;
        }

        public void SetValue(long value)
        {
            if (null != _performanceCounter)
                _performanceCounter.RawValue = value;
        }

        public void SetValue(int value)
        {
            if (null != _performanceCounter)
                _performanceCounter.RawValue = value;
        }
    }
}
