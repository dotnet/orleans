using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;

namespace Orleans.Transactions.TestKit
{
    /// <summary>
    /// Verifies transaction calls when transaction support is disabled.
    /// </summary>
    public abstract class DisabledTransactionsTestRunner : TransactionTestRunnerBase
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="DisabledTransactionsTestRunner"/> class.
        /// </summary>
        /// <param name="grainFactory">The grain factory used to access test grains.</param>
        /// <param name="output">The callback used to write test output.</param>
        protected DisabledTransactionsTestRunner(IGrainFactory grainFactory, Action<string> output)
        : base(grainFactory, output) { }

        /// <summary>
        /// Verifies that invoking a transaction on a transaction grain reports that transactions are disabled.
        /// </summary>
        /// <param name="transactionTestGrainClassName">The class name of the transaction test grain to invoke.</param>
        public virtual void TransactionGrainsThrowWhenTransactions(string transactionTestGrainClassName)
        {
            const int delta = 5;
            ITransactionTestGrain grain = RandomTestGrain(transactionTestGrainClassName);
            Func<Task> task = () => grain.Set(delta);
            var response = task.Should().ThrowAsync<OrleansTransactionsDisabledException>();
        }

        /// <summary>
        /// Verifies that invoking a transaction across multiple transaction grains reports that transactions are disabled.
        /// </summary>
        /// <param name="transactionTestGrainClassName">The class name of the transaction test grains to invoke.</param>
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
            var response = task.Should().ThrowAsync<OrleansTransactionsDisabledException>();
        }
    }
}
