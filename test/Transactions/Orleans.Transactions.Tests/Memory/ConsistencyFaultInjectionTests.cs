using Orleans.TestingHost;
using Orleans.Transactions.Tests.Consistency;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace Orleans.Transactions.Tests.Memory
{
    [TestCategory("Transactions")]
    public class ConsistencyFaultInjectionTests: ConsistencyTransactionTestRunner, IClassFixture<FaultInjectedMemoryTransactionsFixture>
    {
        public ConsistencyFaultInjectionTests(FaultInjectedMemoryTransactionsFixture fixture, ITestOutputHelper output)
            : base(fixture.GrainFactory, output)
        { }

        protected override bool StorageErrorInjectionActive => true;
        protected override bool StorageAdaptorHasLimitedCommitSpace => false;
    }
}
