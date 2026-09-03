using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;

namespace Orleans.Transactions.TestKit
{
    /// <summary>
    /// Runs concurrent transaction scenarios with shared grain participants.
    /// </summary>
    public abstract class TransactionConcurrencyTestRunner : TransactionTestRunnerBase
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="TransactionConcurrencyTestRunner"/> class.
        /// </summary>
        /// <param name="grainFactory">The grain factory used to access test grains.</param>
        /// <param name="output">The callback used to write test output.</param>
        protected TransactionConcurrencyTestRunner(IGrainFactory grainFactory, Action<string> output)
        : base(grainFactory, output) { }

        /// <summary>
        /// Verifies two concurrent transactions which share one grain.
        /// </summary>
        /// <param name="grainStates">The transaction state configuration used to select test grains.</param>
        /// <returns>A task which represents the test.</returns>
        public virtual async Task SingleSharedGrainTest(string grainStates)
        {
            const int expected = 5;

            ITransactionTestGrain grain1 = RandomTestGrain(grainStates);
            ITransactionTestGrain grain2 = RandomTestGrain(grainStates);
            ITransactionTestGrain sharedGrain = RandomTestGrain(grainStates);
            List<ITransactionTestGrain> transaction1Members = new List<ITransactionTestGrain>(new[] { grain1, sharedGrain });
            List<ITransactionTestGrain> transaction2Members = new List<ITransactionTestGrain>(new[] { grain2, sharedGrain });

            ITransactionCoordinatorGrain coordinator1 = this.grainFactory.GetGrain<ITransactionCoordinatorGrain>(Guid.NewGuid());
            ITransactionCoordinatorGrain coordinator2 = this.grainFactory.GetGrain<ITransactionCoordinatorGrain>(Guid.NewGuid());
            await RunConcurrentTransactions(
                () => coordinator1.MultiGrainAdd(transaction1Members, expected),
                () => coordinator2.MultiGrainAdd(transaction2Members, expected));

            int[] actual = await grain1.Get();
            expected.Should().Be(actual.FirstOrDefault());
            actual = await grain2.Get();
            expected.Should().Be(actual.FirstOrDefault());
            actual = await sharedGrain.Get();
            actual.FirstOrDefault().Should().Be(expected * 2);
        }

        /// <summary>
        /// Verifies a chain of concurrent transactions in which adjacent transactions share a grain.
        /// </summary>
        /// <param name="grainStates">The transaction state configuration used to select test grains.</param>
        /// <returns>A task which represents the test.</returns>
        public virtual async Task TransactionChainTest(string grainStates)
        {
            const int expected = 5;

            ITransactionTestGrain grain1 = RandomTestGrain(grainStates);
            ITransactionTestGrain grain2 = RandomTestGrain(grainStates);
            ITransactionTestGrain grain3 = RandomTestGrain(grainStates);
            ITransactionTestGrain grain4 = RandomTestGrain(grainStates);
            ITransactionTestGrain grain5 = RandomTestGrain(grainStates);
            List<ITransactionTestGrain> transaction1Members = new List<ITransactionTestGrain>(new[] { grain1, grain2 });
            List<ITransactionTestGrain> transaction2Members = new List<ITransactionTestGrain>(new[] { grain2, grain3 });
            List<ITransactionTestGrain> transaction3Members = new List<ITransactionTestGrain>(new[] { grain3, grain4 });
            List<ITransactionTestGrain> transaction4Members = new List<ITransactionTestGrain>(new[] { grain4, grain5 });

            ITransactionCoordinatorGrain coordinator1 = this.grainFactory.GetGrain<ITransactionCoordinatorGrain>(Guid.NewGuid());
            ITransactionCoordinatorGrain coordinator2 = this.grainFactory.GetGrain<ITransactionCoordinatorGrain>(Guid.NewGuid());
            ITransactionCoordinatorGrain coordinator3 = this.grainFactory.GetGrain<ITransactionCoordinatorGrain>(Guid.NewGuid());
            ITransactionCoordinatorGrain coordinator4 = this.grainFactory.GetGrain<ITransactionCoordinatorGrain>(Guid.NewGuid());
            await RunConcurrentTransactions(
                () => coordinator1.MultiGrainAdd(transaction1Members, expected),
                () => coordinator2.MultiGrainAdd(transaction2Members, expected),
                () => coordinator3.MultiGrainAdd(transaction3Members, expected),
                () => coordinator4.MultiGrainAdd(transaction4Members, expected));

            int[] actual = await grain1.Get();
            actual.FirstOrDefault().Should().Be(expected);
            actual = await grain2.Get();
            actual.FirstOrDefault().Should().Be(expected * 2);
            actual = await grain3.Get();
            actual.FirstOrDefault().Should().Be(expected * 2);
            actual = await grain4.Get();
            actual.FirstOrDefault().Should().Be(expected * 2);
            actual = await grain5.Get();
            actual.FirstOrDefault().Should().Be(expected);
        }

        /// <summary>
        /// Verifies a tree of concurrent transactions in which one transaction joins participants from two others.
        /// </summary>
        /// <param name="grainStates">The transaction state configuration used to select test grains.</param>
        /// <returns>A task which represents the test.</returns>
        public virtual async Task TransactionTreeTest(string grainStates)
        {
            const int expected = 5;

            ITransactionTestGrain grain1 = RandomTestGrain(grainStates);
            ITransactionTestGrain grain2 = RandomTestGrain(grainStates);
            ITransactionTestGrain grain3 = RandomTestGrain(grainStates);
            ITransactionTestGrain grain4 = RandomTestGrain(grainStates);
            List<ITransactionTestGrain> transaction1Members = new List<ITransactionTestGrain>(new[] { grain1, grain2 });
            List<ITransactionTestGrain> transaction2Members = new List<ITransactionTestGrain>(new[] { grain3, grain4 });
            List<ITransactionTestGrain> transaction3Members = new List<ITransactionTestGrain>(new[] { grain2, grain3 });

            ITransactionCoordinatorGrain coordinator1 = this.grainFactory.GetGrain<ITransactionCoordinatorGrain>(Guid.NewGuid());
            ITransactionCoordinatorGrain coordinator2 = this.grainFactory.GetGrain<ITransactionCoordinatorGrain>(Guid.NewGuid());
            ITransactionCoordinatorGrain coordinator3 = this.grainFactory.GetGrain<ITransactionCoordinatorGrain>(Guid.NewGuid());
            await RunConcurrentTransactions(
                () => coordinator1.MultiGrainAdd(transaction1Members, expected),
                () => coordinator2.MultiGrainAdd(transaction2Members, expected),
                () => coordinator3.MultiGrainAdd(transaction3Members, expected));

            int[] actual = await grain1.Get();
            actual.FirstOrDefault().Should().Be(expected);
            actual = await grain2.Get();
            actual.FirstOrDefault().Should().Be(expected * 2);
            actual = await grain3.Get();
            actual.FirstOrDefault().Should().Be(expected * 2);
            actual = await grain4.Get();
            actual.FirstOrDefault().Should().Be(expected);
        }

        private async Task RunConcurrentTransactions(params Func<Task>[] transactions)
        {
            var completed = await Task.WhenAll(transactions.Select(TryRunTransaction));
            // Preserve contention in the first attempt, then serialize retries so aborted operations can make progress.
            for (var i = 0; i < transactions.Length; i++)
            {
                while (!completed[i])
                {
                    completed[i] = await TryRunTransaction(transactions[i]);
                }
            }
        }

        private async Task<bool> TryRunTransaction(Func<Task> transaction)
        {
            try
            {
                await transaction();
                return true;
            }
            catch (OrleansTransactionTransientFailureException exception)
            {
                this.testOutput($"Transaction aborted transiently: {exception.Message}. Retrying.");
                return false;
            }
        }
    }
}
