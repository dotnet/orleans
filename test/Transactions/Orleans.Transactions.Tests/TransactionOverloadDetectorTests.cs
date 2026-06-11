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
            var timeProvider = new TestTimeProvider(DateTimeOffset.UtcNow);
            ITransactionOverloadDetector detector = new TransactionOverloadDetector(statistics, Options.Create(options), timeProvider);
            var start = timeProvider.GetUtcNow();
            var attemptInterval = TimeSpan.FromTicks(TimeSpan.TicksPerSecond / 10_000);
            long total = 0;
            while (timeProvider.GetUtcNow() - start < runTime)
            {
                total++;
                if (!detector.IsOverloaded())
                {
                    statistics.TrackTransactionStarted();
                }

                timeProvider.Advance(attemptInterval);
            }

            var elapsed = timeProvider.GetUtcNow() - start;
            double averageRate = statistics.TransactionsStarted / elapsed.TotalSeconds;
            this.output.WriteLine($"Average of {averageRate}, with target of {options.Limit}. Performed {statistics.TransactionsStarted} transactions of a max of {total} in {elapsed.TotalMilliseconds} simulated ms.");
            // check to make sure average rate is withing rate +- 10%
            Assert.True(options.Limit * 0.9 <= averageRate);
            Assert.True(options.Limit * 1.1 >= averageRate);
        }

        private sealed class TestTimeProvider(DateTimeOffset utcNow) : TimeProvider
        {
            private DateTimeOffset _utcNow = utcNow;

            public override DateTimeOffset GetUtcNow() => _utcNow;

            public void Advance(TimeSpan timeSpan) => _utcNow += timeSpan;
        }
    }
}
