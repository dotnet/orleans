using System.Diagnostics;
using Microsoft.Extensions.Options;
using Xunit.Abstractions;
using Xunit;
using Orleans.Transactions.Abstractions;

namespace Orleans.Transactions.Tests
{
    [TestCategory("BVT"), TestCategory("Transactions")]
    public class TransactionOverloadDetectorTests
    {
        private readonly ITestOutputHelper output;

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
            ITransactionAgentStatistics statistics = new TransactionAgentStatistics();
            ITransactionOverloadDetector detector = new TransactionOverloadDetector(statistics, Options.Create(options));
            Stopwatch sw = Stopwatch.StartNew();
            long total = 0;
            while (sw.Elapsed < runTime)
            {
                total++;
                if (!detector.IsOverloaded())
                {
                    statistics.TrackTransactionStarted();
                }
            }
            sw.Stop();
            double averageRate = (statistics.TransactionsStarted * 1000) / sw.ElapsedMilliseconds;
            this.output.WriteLine($"Average of {averageRate}, with target of {options.Limit}.  Performed {statistics.TransactionsStarted} transactions of a max of {total} in {sw.ElapsedMilliseconds}ms.");
            // check to make sure average rate is withing rate +- 10%
            Assert.True(options.Limit * 0.9 <= averageRate);
            Assert.True(options.Limit * 1.1 >= averageRate);
        }
    }
}
