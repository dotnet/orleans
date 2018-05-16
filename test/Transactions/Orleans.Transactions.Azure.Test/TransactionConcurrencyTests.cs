using Xunit.Abstractions;
using Xunit;
using Orleans.Transactions.Tests;

namespace Orleans.Transactions.AzureStorage.Tests
{
    [TestCategory("Azure"), TestCategory("Transactions"), TestCategory("Functional")]
    public class TransactionConcurrencyTests : TransactionConcurrencyTestRunner, IClassFixture<TestFixture>
    {
        public TransactionConcurrencyTests(TestFixture fixture, ITestOutputHelper output)
            : base(fixture.GrainFactory, output)
        {
            fixture.EnsurePreconditionsMet();
        }
    }
}
