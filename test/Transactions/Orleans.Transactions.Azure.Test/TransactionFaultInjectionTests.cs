using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Orleans.Transactions.AzureStorage.Tests;
using Orleans.Transactions.Tests;
using Xunit;
using Xunit.Abstractions;

namespace Orleans.Transactions.Azure.Tests
{
    [TestCategory("Azure"), TestCategory("Transactions")]
    public class TransactionFaultInjectionTests : FaultInjectionTransactionTestRunner, IClassFixture<FaultInjectionTestFixture>
    {
        public TransactionFaultInjectionTests(FaultInjectionTestFixture fixture, ITestOutputHelper output)
            : base(fixture.GrainFactory, output)
        {
            fixture.EnsurePreconditionsMet();
        }
    }
}
