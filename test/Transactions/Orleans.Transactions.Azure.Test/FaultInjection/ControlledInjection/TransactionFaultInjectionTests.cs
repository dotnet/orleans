using Orleans.Transactions.AzureStorage.Tests;
using Orleans.Transactions.TestKit.xUnit;
using Xunit;
using Xunit.Abstractions;

namespace Orleans.Transactions.Azure.Tests
{
    [TestCategory("AzureStorage"), TestCategory("Transactions")]
    public class TransactionFaultInjectionTests : ControlledFaultInjectionTransactionTestRunnerxUnit, IClassFixture<ControlledFaultInjectionTestFixture>
    {
        public TransactionFaultInjectionTests(ControlledFaultInjectionTestFixture fixture, ITestOutputHelper output)
            : base(fixture.GrainFactory, output)
        {
            fixture.EnsurePreconditionsMet();
        }
    }
}
