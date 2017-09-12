using Xunit;
using Xunit.Abstractions;
using TestExtensions;

namespace Orleans.Transactions.Tests
{
    [TestCategory("BVT"), TestCategory("Transactions")]
    public class DisabledTransactionsTests : DisabledTransactionsTestRunner, IClassFixture<DefaultClusterFixture>
    {
        public DisabledTransactionsTests(DefaultClusterFixture fixture, ITestOutputHelper output)
            : base(fixture.GrainFactory, output)
        {
        }
    }
}
