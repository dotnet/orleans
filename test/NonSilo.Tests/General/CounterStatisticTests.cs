using System;
using System.Threading.Tasks;
using Orleans.Runtime;
using Xunit;
using Xunit.Abstractions;

namespace UnitTests.General
{
    public class CounterStatisticTest : IDisposable
    {
        private readonly ITestOutputHelper output;
        private CounterStatistic[] counters;
        
        public CounterStatisticTest(ITestOutputHelper output)
        {
            this.output = output;
            counters = new CounterStatistic[Environment.ProcessorCount];
        }

        public virtual void Dispose()
        {
            for (int i = 0; i < counters.Length; i++)
            {
                CounterStatistic.Delete("test" + i);                
            }
        }

        [Fact, TestCategory("Functional"), TestCategory("Statistics")]
        public void TestMultithreadedCorrectness()
        {
            int numOfIterations = 1000000;

            Parallel.For(0, Environment.ProcessorCount, j =>
            {
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
                output.WriteLine(""+ counter.GetCurrentValue());
                
                Assert.Equal(i*Environment.ProcessorCount*numOfIterations, counter.GetCurrentValue());
            }
        }

    }
}
