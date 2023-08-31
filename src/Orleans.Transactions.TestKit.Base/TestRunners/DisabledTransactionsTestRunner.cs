using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;

namespace Orleans.Transactions.TestKit
{
    public abstract class DisabledTransactionsTestRunner : TransactionTestRunnerBase
    {
        protected DisabledTransactionsTestRunner(IGrainFactory grainFactory, Action<string> output)
        : base(grainFactory, output) { }

        public virtual void TransactionGrainsThrowWhenTransactions(string transactionTestGrainClassName)
        {
            const int delta = 5;
            var grain = RandomTestGrain(transactionTestGrainClassName);
            Func<Task> task = ()=>grain.Set(delta);
            var response = task.Should().ThrowAsync<OrleansTransactionsDisabledException>();
        }

        public virtual void MultiTransactionGrainsThrowWhenTransactions(string transactionTestGrainClassName)
        {
            const int delta = 5;
            const int grainCount = TransactionTestConstants.MaxCoordinatedTransactions;

            var grains =
                Enumerable.Range(0, grainCount)
                    .Select(i => RandomTestGrain(transactionTestGrainClassName))
                    .ToList();
            var coordinator = this.grainFactory.GetGrain<ITransactionCoordinatorGrain>(Guid.NewGuid());

            Func<Task> task = () => coordinator.MultiGrainSet(grains, delta);
            var response = task.Should().ThrowAsync<OrleansTransactionsDisabledException>();
        }
    }
}
