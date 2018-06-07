using System;
using System.Diagnostics;
using Microsoft.Extensions.Options;
using Xunit.Abstractions;
using Xunit;
using Orleans.Configuration;
using Orleans.TestingHost.Utils;

namespace Orleans.Transactions.Tests
{
    [TestCategory("BVT"), TestCategory("Transactions")]
    public class TransactionOverloadDetectorTests
    {
        private ITestOutputHelper output;

        public TransactionOverloadDetectorTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        [SkippableTheory]
        [InlineData(60, TransactionRateLoadSheddingOptions.DEFAULT_LIMIT)]
        [InlineData(60, TransactionRateLoadSheddingOptions.DEFAULT_LIMIT * 2)]
        [InlineData(60, 1)]
        public void RateLimitTest(int runTimeInSeconds, double limit)
        {
            TimeSpan runTime = TimeSpan.FromSeconds(runTimeInSeconds);
            TransactionRateLoadSheddingOptions options = new TransactionRateLoadSheddingOptions { Enabled = true, Limit = limit };
            TransactionAgentStatistics statistics = new TransactionAgentStatistics(NullTelemetryProducer.Instance, Options.Create(new StatisticsOptions()));
            ITransactionOverloadDetector detector = new TransactionOverloadDetector(statistics, Options.Create(options));
            Stopwatch sw = Stopwatch.StartNew();
            long total = 0;
            while (sw.Elapsed < runTime)
            {
                total++;
                if (!detector.IsOverloaded())
                {
                    statistics.TransactionStartedCounter++;
                }
            }
            sw.Stop();
            double averageRate = (statistics.TransactionStartedCounter * 1000) / sw.ElapsedMilliseconds;
            this.output.WriteLine($"Average of {averageRate}, with target of {options.Limit}.  Performed {statistics.TransactionStartedCounter} transactions of a max of {total} in {sw.ElapsedMilliseconds}ms.");
            // check to make sure average rate is withing rate +- 10%
            Assert.True(options.Limit * 0.9 <= averageRate);
            Assert.True(options.Limit * 1.1 >= averageRate);
        }
    }
}
