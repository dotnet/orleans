using Orleans.Transactions.TestKit.xUnit;
using Xunit;
using Xunit.Abstractions;

namespace Orleans.Transactions.DynamoDB.Tests
{
    /// <summary>
    /// Tests for transaction golden path scenarios with DynamoDB.
    /// </summary>
    [TestCategory("DynamoDB"), TestCategory("Transactions"), TestCategory("Functional")]
    public class GoldenPathTests : GoldenPathTransactionTestRunnerxUnit, IClassFixture<TestFixture>
    {
        public GoldenPathTests(TestFixture fixture, ITestOutputHelper output)
            : base(fixture.GrainFactory, output)
        {
            fixture.EnsurePreconditionsMet();
        }
    }
}
