using Xunit;
using Xunit.Abstractions;
using Orleans.Transactions.Tests;

namespace Orleans.Transactions.DynamoDB.Tests
{
    [TestCategory("DynamoDb"), TestCategory("Transactions"), TestCategory("Functional")]
    public class GoldenPathTests : GoldenPathTransactionTestRunner, IClassFixture<TestFixture>
    {
        public GoldenPathTests(TestFixture fixture, ITestOutputHelper output)
            : base(fixture.GrainFactory, output)
        {
            fixture.EnsurePreconditionsMet();
        }
    }
}
