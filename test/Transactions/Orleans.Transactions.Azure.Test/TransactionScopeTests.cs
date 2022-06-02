using Orleans.Transactions.TestKit.xUnit;

using Xunit;
using Xunit.Abstractions;

namespace Orleans.Transactions.AzureStorage.Tests
{
    [TestCategory("AzureStorage"), TestCategory("Transactions"), TestCategory("Functional")]
    public class TransactionScopeTests : ScopedTransactionsTestRunnerxUnit, IClassFixture<TestFixture>
    {
        public TransactionScopeTests(TestFixture fixture, ITestOutputHelper output)
            : base(fixture.GrainFactory, output)
        {
            fixture.EnsurePreconditionsMet();
        }
    }

}
