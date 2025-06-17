using Orleans.Transactions.TestKit.xUnit;
using Xunit;
using Xunit.Abstractions;
using TestExtensions;

namespace Orleans.Transactions.Tests
{
    /// <summary>
    /// Tests for behavior when transactions are disabled.
    /// </summary>
    [TestCategory("BVT"), TestCategory("Transactions")]
    public class DisabledTransactionsTests : DisabledTransactionsTestRunnerxUnit, IClassFixture<DefaultClusterFixture>
    {
        public DisabledTransactionsTests(DefaultClusterFixture fixture, ITestOutputHelper output)
            : base(fixture.GrainFactory, output)
        {
        }
    }
}
