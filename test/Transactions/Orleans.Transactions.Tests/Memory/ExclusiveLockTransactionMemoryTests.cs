using Orleans.Transactions.TestKit.xUnit;
using Xunit;

namespace Orleans.Transactions.Tests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Transactions")]
[TestCategory("BVT"), TestCategory("Transactions")]
public class ExclusiveLockTransactionMemoryTests : ExclusiveLockTransactionTestRunnerxUnit, IClassFixture<MemoryTransactionsFixture>
{
    public ExclusiveLockTransactionMemoryTests(MemoryTransactionsFixture fixture, ITestOutputHelper output)
        : base(fixture.GrainFactory, output)
    {
    }
}
