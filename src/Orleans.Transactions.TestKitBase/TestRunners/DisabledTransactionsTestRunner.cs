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
            ITransactionTestGrain grain = RandomTestGrain(transactionTestGrainClassName);
            Func<Task> task = ()=>grain.Set(delta);
            var response = task.ShouldThrow<OrleansTransactionsDisabledException>();
        }

        public virtual void MultiTransactionGrainsThrowWhenTransactions(string transactionTestGrainClassName)
        {
            const int delta = 5;
            const int grainCount = TransactionTestConstants.MaxCoordinatedTransactions;

            List<ITransactionTestGrain> grains =
                Enumerable.Range(0, grainCount)
                    .Select(i => RandomTestGrain(transactionTestGrainClassName))
                    .ToList();
            ITransactionCoordinatorGrain coordinator = this.grainFactory.GetGrain<ITransactionCoordinatorGrain>(Guid.NewGuid());

            Func<Task> task = () => coordinator.MultiGrainSet(grains, delta);
            var response = task.ShouldThrow<OrleansTransactionsDisabledException>();
        }
    }
}
