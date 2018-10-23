﻿using Xunit.Abstractions;
using Xunit;
using Orleans.Transactions.Tests;

namespace Orleans.Transactions.AzureStorage.Tests
{
    [TestCategory("Azure"), TestCategory("Transactions")]
    public class ConsistencySkewedClockTests : ConsistencyTransactionTestRunner, IClassFixture<SkewedClockTestFixture>
    {
        public ConsistencySkewedClockTests(SkewedClockTestFixture fixture, ITestOutputHelper output)
            : base(fixture.GrainFactory, output)
        {
        }

        protected override bool StorageAdaptorHasLimitedCommitSpace => true;
        protected override bool StorageErrorInjectionActive => false;
    }
}
