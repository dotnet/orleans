using Xunit.Abstractions;
using Xunit;

namespace Orleans.Transactions.Tests
{
    [TestCategory("BVT"), TestCategory("Transactions")]
    public class MemoryOrchestrationsTransactionsTests : OrchestrationsTransactionsTestRunner, IClassFixture<MemoryTransactionsFixture>
    {
        public MemoryOrchestrationsTransactionsTests(MemoryTransactionsFixture fixture, ITestOutputHelper output)
            : base(fixture.GrainFactory, output)
        {
        }
    }
}
