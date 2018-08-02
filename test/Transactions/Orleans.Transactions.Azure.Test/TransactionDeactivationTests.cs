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
    public class TransactionDeactivationTests : GrainDeactivationTransactionTestRunner, IClassFixture<DeactivationTestFixture>
    {
        public TransactionDeactivationTests(DeactivationTestFixture fixture, ITestOutputHelper output)
            : base(fixture.GrainFactory, output)
        {
            fixture.EnsurePreconditionsMet();
        }
    }
}
