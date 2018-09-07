using Xunit.Abstractions;
using Xunit;
using Orleans.Transactions.Tests;

namespace Orleans.Transactions.AzureStorage.Tests
{
    [TestCategory("Azure"), TestCategory("Transactions")]
    public class ConsistencyTests : ConsistencyTransactionTestRunner, IClassFixture<TestFixture>
    {
        public ConsistencyTests(TestFixture fixture, ITestOutputHelper output)
            : base(fixture.GrainFactory, output)
        {
        }

        protected override bool StorageAdaptorHasLimitedCommitSpace => true;
        protected override bool StorageErrorInjectionActive => false;
    }
}
