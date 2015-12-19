using System;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans.Runtime;

namespace UnitTests.General
{
    [TestClass]
    public class CounterStatisticTest
    {
        private CounterStatistic[] counters;
        
        [TestInitialize]
        public void InitializeForTesting()
        {
            counters = new CounterStatistic[Environment.ProcessorCount];
        }

        [TestCleanup]
        public void Clean()
        {
            for (int i = 0; i < counters.Length; i++)
            {
                CounterStatistic.Delete("test" + i);                
            }
            counters = null;
        }

        [TestMethod, TestCategory("Functional"), TestCategory("Statistics")]
        public void TestMultithreadedCorrectness()
        {
            int numOfIterations = 1000000;

            Parallel.For(0, Environment.ProcessorCount, j =>
            {
                CounterStatistic.SetOrleansManagedThread();

                for (int i = 0; i < counters.Length; i++)
                {
                    counters[i] = CounterStatistic.FindOrCreate(new StatisticName("test" + i));

                    for (int k = 0; k < numOfIterations; k++)
                    {
                        counters[i].IncrementBy(i);
                    }
                }
            });

            for (int i = 0; i < counters.Length; i++)
            {
                var counter = CounterStatistic.FindOrCreate(new StatisticName("test" + i));
                Console.WriteLine(""+ counter.GetCurrentValue());
                
                Assert.AreEqual(i*Environment.ProcessorCount*numOfIterations, counter.GetCurrentValue());
            }
        }

    }
}
