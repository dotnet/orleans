using Xunit;
using Xunit.Abstractions;
using Orleans.Transactions.Tests;

namespace Orleans.Transactions.Azure.Tests
{
    [TestCategory("Azure"), TestCategory("Transactions"), TestCategory("Functional")]
    public class GoldenPathTests : GoldenPathTransactionTestRunner, IClassFixture<TestFixture>
    {
        public GoldenPathTests(TestFixture fixture, ITestOutputHelper output)
            : base(fixture.GrainFactory, output)
        {
        }
    }
}
