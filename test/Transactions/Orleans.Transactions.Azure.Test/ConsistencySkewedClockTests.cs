using Orleans.Transactions.TestKit.xUnit;
using Xunit.Abstractions;
using Xunit;

namespace Orleans.Transactions.AzureStorage.Tests
{
    [TestCategory("Azure"), TestCategory("Transactions-dev")]
    public class ConsistencySkewedClockTests : ConsistencyTransactionTestRunnerxUnit, IClassFixture<SkewedClockTestFixture>
    {
        public ConsistencySkewedClockTests(SkewedClockTestFixture fixture, ITestOutputHelper output)
            : base(fixture.GrainFactory, output)
        {
        }

        protected override bool StorageAdaptorHasLimitedCommitSpace => true;
        protected override bool StorageErrorInjectionActive => false;
    }
}
