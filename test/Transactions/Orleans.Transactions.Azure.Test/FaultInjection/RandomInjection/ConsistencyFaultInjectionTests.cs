using Orleans.TestingHost;
using Orleans.Transactions.Tests;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Transactions.TestKit.xUnit;
using Xunit;
using Xunit.Abstractions;

namespace Orleans.Transactions.AzureStorage.Tests
{
    [TestCategory("Azure"), TestCategory("Transactions-dev")]
    public class ConsistencyFaultInjectionTests: ConsistencyTransactionTestRunnerxUnit, IClassFixture<RandomFaultInjectedTestFixture>
    {
        public ConsistencyFaultInjectionTests(RandomFaultInjectedTestFixture fixture, ITestOutputHelper output)
            : base(fixture.GrainFactory, output)
        { }

        protected override bool StorageAdaptorHasLimitedCommitSpace => true;
        protected override bool StorageErrorInjectionActive => true;
    }
}
