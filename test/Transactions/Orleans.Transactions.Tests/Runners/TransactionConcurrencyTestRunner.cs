using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace Orleans.Transactions.Tests
{
    public abstract class TransactionConcurrencyTestRunner : TransactionTestRunnerBase
    {
        protected TransactionConcurrencyTestRunner(IGrainFactory grainFactory, ITestOutputHelper output)
        : base(grainFactory, output) { }

        /// <summary>
        /// Two transaction share a single grain
        /// </summary>
        /// <param name="grainStates"></param>
        /// <returns></returns>
        [SkippableTheory]
        [InlineData(TransactionTestConstants.SingleStateTransactionalGrain)]
        [InlineData(TransactionTestConstants.DoubleStateTransactionalGrain)]
        [InlineData(TransactionTestConstants.MaxStateTransactionalGrain)]
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
            await Task.WhenAll(
                coordinator1.MultiGrainAdd(transaction1Members, expected),
                coordinator2.MultiGrainAdd(transaction2Members, expected));

            int[] actual = await grain1.Get();
            Assert.Equal(expected, actual.FirstOrDefault());
            actual = await grain2.Get();
            Assert.Equal(expected, actual.FirstOrDefault());
            actual = await sharedGrain.Get();
            Assert.Equal(expected*2, actual.FirstOrDefault());
        }

        /// <summary>
        /// Chain of transactions, each dependent on the results of the previous
        /// </summary>
        /// <param name="grainStates"></param>
        /// <returns></returns>
        [SkippableTheory]
        [InlineData(TransactionTestConstants.SingleStateTransactionalGrain)]
        [InlineData(TransactionTestConstants.DoubleStateTransactionalGrain)]
        [InlineData(TransactionTestConstants.MaxStateTransactionalGrain)]
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
            await Task.WhenAll(
                coordinator1.MultiGrainAdd(transaction1Members, expected),
                coordinator2.MultiGrainAdd(transaction2Members, expected),
                coordinator3.MultiGrainAdd(transaction3Members, expected),
                coordinator4.MultiGrainAdd(transaction4Members, expected));

            int[] actual = await grain1.Get();
            Assert.Equal(expected, actual.FirstOrDefault());
            actual = await grain2.Get();
            Assert.Equal(expected*2, actual.FirstOrDefault());
            actual = await grain3.Get();
            Assert.Equal(expected*2, actual.FirstOrDefault());
            actual = await grain4.Get();
            Assert.Equal(expected*2, actual.FirstOrDefault());
            actual = await grain5.Get();
            Assert.Equal(expected, actual.FirstOrDefault());
        }

        /// <summary>
        /// Single transaction containing two grains is dependant on two other transaction, one from each grain
        /// </summary>
        /// <param name="grainStates"></param>
        /// <returns></returns>
        [SkippableTheory]
        [InlineData(TransactionTestConstants.SingleStateTransactionalGrain)]
        [InlineData(TransactionTestConstants.DoubleStateTransactionalGrain)]
        [InlineData(TransactionTestConstants.MaxStateTransactionalGrain)]
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
            await Task.WhenAll(
                coordinator1.MultiGrainAdd(transaction1Members, expected),
                coordinator2.MultiGrainAdd(transaction2Members, expected),
                coordinator3.MultiGrainAdd(transaction3Members, expected));

            int[] actual = await grain1.Get();
            Assert.Equal(expected, actual.FirstOrDefault());
            actual = await grain2.Get();
            Assert.Equal(expected * 2, actual.FirstOrDefault());
            actual = await grain3.Get();
            Assert.Equal(expected * 2, actual.FirstOrDefault());
            actual = await grain4.Get();
            Assert.Equal(expected, actual.FirstOrDefault());
        }
    }
}
