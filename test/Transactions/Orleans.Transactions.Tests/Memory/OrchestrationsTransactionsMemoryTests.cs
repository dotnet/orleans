using Xunit.Abstractions;
using Xunit;

namespace Orleans.Transactions.Tests
{
    [TestCategory("BVT"), TestCategory("Transactions")]
    public class OrchestrationsTransactionsMemoryTests : OrchestrationsTransactionsTestRunner, IClassFixture<MemoryTransactionsFixture>
    {
        public OrchestrationsTransactionsMemoryTests(MemoryTransactionsFixture fixture, ITestOutputHelper output)
            : base(fixture.GrainFactory, output)
        {
        }
    }
}
