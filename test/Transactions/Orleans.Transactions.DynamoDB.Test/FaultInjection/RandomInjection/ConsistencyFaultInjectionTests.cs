using Orleans.Transactions.TestKit.xUnit;
using Xunit;
using Xunit.Abstractions;

namespace Orleans.Transactions.DynamoDB.Tests
{
    /// <summary>
    /// Tests for transaction consistency with random fault injection using DynamoDB.
    /// </summary>
    [TestCategory("DynamoDB"), TestCategory("Transactions-dev")]
    public class ConsistencyFaultInjectionTests: ConsistencyTransactionTestRunnerxUnit, IClassFixture<RandomFaultInjectedTestFixture>
    {
        public ConsistencyFaultInjectionTests(RandomFaultInjectedTestFixture fixture, ITestOutputHelper output)
            : base(fixture.GrainFactory, output)
        { }

        protected override bool StorageAdaptorHasLimitedCommitSpace => true;
        protected override bool StorageErrorInjectionActive => true;
    }
}
