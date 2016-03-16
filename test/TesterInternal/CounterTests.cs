using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using Orleans.Runtime;
using System;
using Xunit;

namespace UnitTests
{
    public class CounterTests : IDisposable
    {
        private static readonly SafeRandom random = new SafeRandom();
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
            Assert.AreEqual(name.ToString(), ctr.Name);
            Assert.IsTrue(ctr.ToString().Contains(name.Name));
            Assert.AreEqual(0, ctr.GetCurrentValue());
        }

        [Fact, TestCategory("Functional"), TestCategory("Management")]
        public void Counter_SetValue()
        {
            StatisticName name = new StatisticName(CounterName);
            int val = random.Next(1000000);
            CounterStatistic ctr = CounterStatistic.FindOrCreate(name);
            ctr.IncrementBy(val);
            Assert.AreEqual(val, ctr.GetCurrentValue());
        }

        [Fact, TestCategory("Functional"), TestCategory("Management")]
        public void Counter_Increment()
        {
            StatisticName name = new StatisticName(CounterName);
            CounterStatistic ctr = CounterStatistic.FindOrCreate(name);
            Assert.AreEqual(0, ctr.GetCurrentValue());
            ctr.Increment();
            Assert.AreEqual(1, ctr.GetCurrentValue());
        }

        [Fact, TestCategory("Functional"), TestCategory("Management")]
        public void Counter_IncrementBy()
        {
            StatisticName name = new StatisticName(CounterName);
            int val = random.Next(1000000);
            CounterStatistic ctr = CounterStatistic.FindOrCreate(name);
            ctr.IncrementBy(val);
            Assert.AreEqual(val, ctr.GetCurrentValue());
            ctr.Increment();
            Assert.AreEqual(val + 1, ctr.GetCurrentValue());
            ctr.Increment();
            Assert.AreEqual(val + 2, ctr.GetCurrentValue());
        }

        [Fact, TestCategory("Functional"), TestCategory("Management")]
        public void Counter_IncrementFromMinInt()
        {
            StatisticName name = new StatisticName(CounterName);
            int val = int.MinValue;
            CounterStatistic ctr = CounterStatistic.FindOrCreate(name);
            ctr.IncrementBy(val);
            Assert.AreEqual(val, ctr.GetCurrentValue());
            ctr.Increment();
            Assert.AreEqual(val + 1, ctr.GetCurrentValue());
            ctr.Increment();
            Assert.AreEqual(val + 2, ctr.GetCurrentValue());
        }

        [Fact, TestCategory("Functional"), TestCategory("Management")]
        public void Counter_IncrementFromMaxInt()
        {
            StatisticName name = new StatisticName(CounterName);
            int val = int.MaxValue;
            long longVal = int.MaxValue;
            Assert.AreEqual(longVal, val);
            CounterStatistic ctr = CounterStatistic.FindOrCreate(name);
            ctr.IncrementBy(val);
            Assert.AreEqual(val, ctr.GetCurrentValue());
            ctr.Increment();
            Assert.AreEqual(longVal + 1, ctr.GetCurrentValue());
            ctr.Increment();
            Assert.AreEqual(longVal + 2, ctr.GetCurrentValue());
        }

        [Fact, TestCategory("Functional"), TestCategory("Management")]
        public void Counter_DecrementBy()
        {
            StatisticName name = new StatisticName(CounterName);
            int startValue = 10;
            int newValue = startValue - 1;
            CounterStatistic ctr = CounterStatistic.FindOrCreate(name);
            ctr.IncrementBy(startValue);
            Assert.AreEqual(startValue, ctr.GetCurrentValue());
            ctr.DecrementBy(1);
            Assert.AreEqual(newValue, ctr.GetCurrentValue());
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
        //    Assert.IsTrue(
        //        AreWindowsPerfCountersAvailable(),
        //        "Orleans perf counters are registered with Windows");
        //}
    }
}
