using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace Orleans.Transactions.Tests
{
    public abstract class DisabledTransactionsTestRunner : TransactionTestRunnerBase
    {
        protected DisabledTransactionsTestRunner(IGrainFactory grainFactory, ITestOutputHelper output)
        : base(grainFactory, output) { }

        [SkippableTheory]
        [InlineData(TransactionTestConstants.NoStateTransactionalGrain)]
        public virtual async Task TransactionGrainsThrowWhenTransactions(string transactionTestGrainClassName)
        {
            const int delta = 5;
            ITransactionTestGrain grain = RandomTestGrain(transactionTestGrainClassName);
            OrleansStartTransactionFailedException exception = await Assert.ThrowsAsync<OrleansStartTransactionFailedException>(() => grain.Set(delta));
            Assert.IsAssignableFrom<OrleansTransactionsDisabledException>(exception.InnerException);
        }

        [SkippableTheory]
        [InlineData(TransactionTestConstants.NoStateTransactionalGrain)]
        public virtual async Task MultiTransactionGrainsThrowWhenTransactions(string transactionTestGrainClassName)
        {
            const int delta = 5;
            const int grainCount = TransactionTestConstants.MaxCoordinatedTransactions;

            List<ITransactionTestGrain> grains =
                Enumerable.Range(0, grainCount)
                    .Select(i => RandomTestGrain(transactionTestGrainClassName))
                    .ToList();
            ITransactionCoordinatorGrain coordinator = this.grainFactory.GetGrain<ITransactionCoordinatorGrain>(Guid.NewGuid());

            OrleansStartTransactionFailedException exception = await Assert.ThrowsAsync<OrleansStartTransactionFailedException>(() => coordinator.MultiGrainSet(grains, delta));
            Assert.IsAssignableFrom<OrleansTransactionsDisabledException>(exception.InnerException);
        }
    }
}
