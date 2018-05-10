﻿using System;
using Xunit.Abstractions;

namespace Orleans.Transactions.Tests
{
    public class TransactionTestRunnerBase
    {
        protected readonly IGrainFactory grainFactory;
        protected readonly ITestOutputHelper output;

        protected TransactionTestRunnerBase(IGrainFactory grainFactory, ITestOutputHelper output)
        {
            this.output = output;
            this.grainFactory = grainFactory;
        }

        protected ITransactionTestGrain RandomTestGrain(string transactionTestGrainClassNames)
        {
            return TestGrain(transactionTestGrainClassNames, Guid.NewGuid());
        }

        protected virtual ITransactionTestGrain TestGrain(string transactionTestGrainClassName, Guid id)
        {
            return grainFactory.GetGrain<ITransactionTestGrain>(id, transactionTestGrainClassName);
        }
    }
}
