using Orleans.Transactions.TestKit.xUnit;
using Xunit;
using Xunit.Abstractions;

namespace Orleans.Transactions.Tests;

[TestCategory("BVT"), TestCategory("Transactions")]
public class ExclusiveLockTransactionMemoryTests : ExclusiveLockTransactionTestRunnerxUnit, IClassFixture<MemoryTransactionsFixture>
{
    public ExclusiveLockTransactionMemoryTests(MemoryTransactionsFixture fixture, ITestOutputHelper output)
        : base(fixture.GrainFactory, output)
    {
    }
}
