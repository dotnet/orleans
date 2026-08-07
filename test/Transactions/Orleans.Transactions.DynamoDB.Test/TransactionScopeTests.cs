using Microsoft.Extensions.DependencyInjection;

using Orleans.Transactions.TestKit.xUnit;

using Xunit;
using Xunit.Abstractions;

namespace Orleans.Transactions.DynamoDB.Tests
{
    /// <summary>
    /// Tests for scoped transaction functionality with DynamoDB.
    /// </summary>
    [TestCategory("DynamoDB"), TestCategory("Transactions"), TestCategory("Functional")]
    public class TransactionScopeTests : ScopedTransactionsTestRunnerxUnit, IClassFixture<TestFixture>
    {
        public TransactionScopeTests(TestFixture fixture, ITestOutputHelper output)
            : base(fixture.GrainFactory, fixture.HostedCluster.ServiceProvider.GetRequiredService<ITransactionClient>(), output)
        {
            fixture.EnsurePreconditionsMet();
        }
    }
}
