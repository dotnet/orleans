using Orleans.Transactions.TestKit.xUnit;
using Xunit.Abstractions;
using Xunit;

namespace Orleans.Transactions.Tests
{
    [TestCategory("Transactions-dev")]
    public class ConsistencySkewedClockTests : ConsistencyTransactionTestRunnerxUnit, IClassFixture<SkewedClockMemoryTransactionsFixture>
    {
        public ConsistencySkewedClockTests(SkewedClockMemoryTransactionsFixture fixture, ITestOutputHelper output)
            : base(fixture.GrainFactory, output)
        {
        }

        protected override bool StorageErrorInjectionActive => false;
        protected override bool StorageAdaptorHasLimitedCommitSpace => false;

    }
}
