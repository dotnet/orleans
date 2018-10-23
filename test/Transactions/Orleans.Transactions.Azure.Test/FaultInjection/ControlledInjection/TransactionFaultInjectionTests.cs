using Orleans.Transactions.AzureStorage.Tests;
using Orleans.Transactions.Tests;
using Xunit;
using Xunit.Abstractions;

namespace Orleans.Transactions.Azure.Tests
{
    [TestCategory("Azure"), TestCategory("Transactions")]
    public class TransactionFaultInjectionTests : ControlledFaultInjectionTransactionTestRunner, IClassFixture<ControlledFaultInjectionTestFixture>
    {
        public TransactionFaultInjectionTests(ControlledFaultInjectionTestFixture fixture, ITestOutputHelper output)
            : base(fixture.GrainFactory, output)
        {
            fixture.EnsurePreconditionsMet();
        }
    }
}
