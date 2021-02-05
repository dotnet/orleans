
using System;
using Orleans.Internal;
using Orleans.Runtime;
using Xunit;

namespace UnitTests
{
    public class CounterTests : IDisposable
    {
        private const string CounterName = "CounterTestsCounter";
        
        public void Dispose()
        {
            CounterStatistic.Delete(CounterName);
        }

        [Fact, TestCategory("Functional"), TestCategory("Management")]
        public void Counter_InitialValue()
        {
            StatisticName name = new StatisticName(CounterName);
            ICounter<long> ctr = CounterStatistic.FindOrCreate(name);
            Assert.Equal(name.ToString(), ctr.Name);
            Assert.Contains(name.Name, ctr.ToString());
            Assert.Equal(0, ctr.GetCurrentValue());
        }

        [Fact, TestCategory("Functional"), TestCategory("Management")]
        public void Counter_SetValue()
        {
            StatisticName name = new StatisticName(CounterName);
            int val = ThreadSafeRandom.Next(1000000);
            CounterStatistic ctr = CounterStatistic.FindOrCreate(name);
            ctr.IncrementBy(val);
            Assert.Equal(val, ctr.GetCurrentValue());
        }

        [Fact, TestCategory("Functional"), TestCategory("Management")]
        public void Counter_Increment()
        {
            StatisticName name = new StatisticName(CounterName);
            CounterStatistic ctr = CounterStatistic.FindOrCreate(name);
            Assert.Equal(0, ctr.GetCurrentValue());
            ctr.Increment();
            Assert.Equal(1, ctr.GetCurrentValue());
        }

        [Fact, TestCategory("Functional"), TestCategory("Management")]
        public void Counter_IncrementBy()
        {
            StatisticName name = new StatisticName(CounterName);
            int val = ThreadSafeRandom.Next(1000000);
            CounterStatistic ctr = CounterStatistic.FindOrCreate(name);
            ctr.IncrementBy(val);
            Assert.Equal(val, ctr.GetCurrentValue());
            ctr.Increment();
            Assert.Equal(val + 1, ctr.GetCurrentValue());
            ctr.Increment();
            Assert.Equal(val + 2, ctr.GetCurrentValue());
        }

        [Fact, TestCategory("Functional"), TestCategory("Management")]
        public void Counter_IncrementFromMinInt()
        {
            StatisticName name = new StatisticName(CounterName);
            int val = int.MinValue;
            CounterStatistic ctr = CounterStatistic.FindOrCreate(name);
            ctr.IncrementBy(val);
            Assert.Equal(val, ctr.GetCurrentValue());
            ctr.Increment();
            Assert.Equal(val + 1, ctr.GetCurrentValue());
            ctr.Increment();
            Assert.Equal(val + 2, ctr.GetCurrentValue());
        }

        [Fact, TestCategory("Functional"), TestCategory("Management")]
        public void Counter_IncrementFromMaxInt()
        {
            StatisticName name = new StatisticName(CounterName);
            int val = int.MaxValue;
            long longVal = int.MaxValue;
            Assert.Equal(longVal, val);
            CounterStatistic ctr = CounterStatistic.FindOrCreate(name);
            ctr.IncrementBy(val);
            Assert.Equal(val, ctr.GetCurrentValue());
            ctr.Increment();
            Assert.Equal(longVal + 1, ctr.GetCurrentValue());
            ctr.Increment();
            Assert.Equal(longVal + 2, ctr.GetCurrentValue());
        }

        [Fact, TestCategory("Functional"), TestCategory("Management")]
        public void Counter_DecrementBy()
        {
            StatisticName name = new StatisticName(CounterName);
            int startValue = 10;
            int newValue = startValue - 1;
            CounterStatistic ctr = CounterStatistic.FindOrCreate(name);
            ctr.IncrementBy(startValue);
            Assert.Equal(startValue, ctr.GetCurrentValue());
            ctr.DecrementBy(1);
            Assert.Equal(newValue, ctr.GetCurrentValue());
        }

        //[Fact]
        //[ExpectedException(typeof(System.Security.SecurityException))]
        //public void AdminRequiredToRegisterCountersWithWindows()
        //{
        //    OrleansCounterBase.RegisterAllCounters();
        //}

        //[Fact]
        //public void RegisterCountersWithWindows()
        //{
        //    OrleansCounterBase.RegisterAllCounters(); // Requires RunAs Administrator
        //    Assert.True(
        //        AreWindowsPerfCountersAvailable(),
        //        "Orleans perf counters are registered with Windows");
        //}
    }
}
