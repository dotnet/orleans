using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Orleans.Transactions.TestKit.Base;
using Xunit;
using Xunit.Abstractions;

namespace Orleans.Transactions.TestKit.xUnit
{
    public class DisabledTransactionsTestRunnerxUnit : DisabledTransactionsTestRunner
    {
        protected DisabledTransactionsTestRunnerxUnit(IGrainFactory grainFactory, ITestOutputHelper output)
        : base(grainFactory, output.WriteLine) { }

        [SkippableTheory]
        [InlineData(TransactionTestConstants.NoStateTransactionalGrain)]
        public override void TransactionGrainsThrowWhenTransactions(string transactionTestGrainClassName)
        {
             base.TransactionGrainsThrowWhenTransactions(transactionTestGrainClassName);
        }

        [SkippableTheory]
        [InlineData(TransactionTestConstants.NoStateTransactionalGrain)]
        public override void MultiTransactionGrainsThrowWhenTransactions(string transactionTestGrainClassName)
        {
            base.MultiTransactionGrainsThrowWhenTransactions(transactionTestGrainClassName);
        }
    }
}
