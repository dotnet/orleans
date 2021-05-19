using Orleans.Transactions.TestKit.xUnit;
using Xunit;
using Xunit.Abstractions;

namespace Orleans.Transactions.AzureStorage.Tests
{
    [TestCategory("Azure"), TestCategory("Transactions"), TestCategory("Functional")]
    public class SkewedClockGoldenPathTransactionTests : GoldenPathTransactionTestRunnerxUnit, IClassFixture<SkewedClockTestFixture>
    {
        public SkewedClockGoldenPathTransactionTests(SkewedClockTestFixture fixture, ITestOutputHelper output)
            : base(fixture.GrainFactory, output)
        {
            fixture.EnsurePreconditionsMet();
        }

    }
}
