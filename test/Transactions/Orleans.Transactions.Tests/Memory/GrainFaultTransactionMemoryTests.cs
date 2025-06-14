using Orleans.Transactions.TestKit.xUnit;
using Xunit.Abstractions;
using Xunit;

namespace Orleans.Transactions.Tests
{
    /// <summary>
    /// Tests for transaction behavior under grain fault conditions with in-memory storage.
    /// </summary>
    [TestCategory("BVT"), TestCategory("Transactions")]
    public class GrainFaultTransactionMemoryTests : GrainFaultTransactionTestRunnerxUnit, IClassFixture<MemoryTransactionsFixture>
    {
        public GrainFaultTransactionMemoryTests(MemoryTransactionsFixture fixture, ITestOutputHelper output)
            : base(fixture.GrainFactory, output)
        {
        }
    }
}
