﻿using Orleans.Transactions.TestKit.xUnit;
using Xunit;
using Xunit.Abstractions;
using Orleans.Transactions.Tests;

namespace Orleans.Transactions.AzureStorage.Tests
{
    [TestCategory("Azure"), TestCategory("Transactions"), TestCategory("Functional")]
    public class GrainFaultTests : GrainFaultTransactionTestRunnerxUnit, IClassFixture<TestFixture>
    {
        public GrainFaultTests(TestFixture fixture, ITestOutputHelper output)
            : base(fixture.GrainFactory, output)
        {
            fixture.EnsurePreconditionsMet();
        }
    }
}
