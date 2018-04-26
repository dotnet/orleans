using Xunit.Abstractions;
using Xunit;
using System;

namespace Orleans.Transactions.Tests.DistributedTM
{
    [TestCategory("BVT"), TestCategory("Transactions")]
    public class GrainFaultTransactionMemoryTests : GrainFaultTransactionTestRunner, IClassFixture<MemoryTransactionsFixture>
    {
        public GrainFaultTransactionMemoryTests(MemoryTransactionsFixture fixture, ITestOutputHelper output)
            : base(fixture.GrainFactory, output, true)
        {
        }
    }
}
