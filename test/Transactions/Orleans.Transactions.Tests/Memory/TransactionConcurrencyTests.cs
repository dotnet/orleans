using Xunit.Abstractions;
using Xunit;

namespace Orleans.Transactions.Tests
{

    [TestCategory("BVT"), TestCategory("Transactions")]
    public class TransactionConcurrencyTests : TransactionConcurrencyTestRunner, IClassFixture<TransactionConcurrencyTests.SkipFixture>
    {
        public TransactionConcurrencyTests(SkipFixture fixture, ITestOutputHelper output)
            : base(fixture.GrainFactory, output)
        {
            fixture.EnsurePreconditionsMet();
        }

        public class SkipFixture : MemoryTransactionsFixture
        {
            protected override void CheckPreconditionsOrThrow()
            {
                throw new SkipException("Test fail against memory storage.  Remove SKipFixture when fixed");
            }
        }
    }
}
