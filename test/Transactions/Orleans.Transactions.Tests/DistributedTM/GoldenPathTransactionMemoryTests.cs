﻿using Xunit.Abstractions;
using Xunit;
using System;

namespace Orleans.Transactions.Tests.DistributedTM
{
    [TestCategory("BVT"), TestCategory("Transactions")]
    public class GoldenPathTransactionMemoryTests : GoldenPathTransactionTestRunner, IClassFixture<MemoryTransactionsFixture>
    {
        public GoldenPathTransactionMemoryTests(MemoryTransactionsFixture fixture, ITestOutputHelper output)
            : base(fixture.GrainFactory, output, true)
        {
        }
    }
}
