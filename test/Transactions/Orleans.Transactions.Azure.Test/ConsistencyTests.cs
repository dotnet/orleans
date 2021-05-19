using Orleans.Transactions.TestKit.xUnit;
using Xunit.Abstractions;
using Xunit;

namespace Orleans.Transactions.AzureStorage.Tests
{
    [TestCategory("Azure"), TestCategory("Transactions-dev")]
    public class ConsistencyTests : ConsistencyTransactionTestRunnerxUnit, IClassFixture<TestFixture>
    {
        public ConsistencyTests(TestFixture fixture, ITestOutputHelper output)
            : base(fixture.GrainFactory, output)
        {
        }

        protected override bool StorageAdaptorHasLimitedCommitSpace => true;
        protected override bool StorageErrorInjectionActive => false;
    }
}
