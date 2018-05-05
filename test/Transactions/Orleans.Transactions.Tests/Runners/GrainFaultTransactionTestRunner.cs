using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace Orleans.Transactions.Tests
{
    public abstract class GrainFaultTransactionTestRunner : TransactionTestRunnerBase
    {
        public GrainFaultTransactionTestRunner(IGrainFactory grainFactory, ITestOutputHelper output, bool distributedTm = false)
        : base(grainFactory, output, distributedTm)
        { }

        [SkippableTheory]
        [InlineData(TransactionTestConstants.TransactionGrainStates.SingleStateTransaction)]
        public async Task AbortTransactionOnExceptions(TransactionTestConstants.TransactionGrainStates grainStates)
        {
            const int expected = 5;

            ITransactionTestGrain grain = RandomTestGrain(grainStates);
            ITransactionCoordinatorGrain coordinator = this.grainFactory.GetGrain<ITransactionCoordinatorGrain>(Guid.NewGuid());

            await coordinator.MultiGrainSet(new List<ITransactionTestGrain> { grain }, expected);
            await Assert.ThrowsAsync<OrleansTransactionAbortedException>(() => coordinator.AddAndThrow(grain, expected));

            await TestAfterDustSettles(async () =>
            {
                int[] actualValues = await grain.Get();
                foreach (var actual in actualValues)
                {
                    Assert.Equal(expected, actual);
                }
            });
        }

        [SkippableTheory]
        [InlineData(TransactionTestConstants.TransactionGrainStates.SingleStateTransaction)]
        public async Task MultiGrainAbortTransactionOnExceptions(TransactionTestConstants.TransactionGrainStates grainStates)
        {
            const int grainCount = TransactionTestConstants.MaxCoordinatedTransactions - 1;
            const int expected = 5;

            ITransactionTestGrain throwGrain = RandomTestGrain(grainStates);
            List<ITransactionTestGrain> grains =
                Enumerable.Range(0, grainCount)
                    .Select(i => RandomTestGrain(grainStates))
                    .ToList();
            ITransactionCoordinatorGrain coordinator = this.grainFactory.GetGrain<ITransactionCoordinatorGrain>(Guid.NewGuid());

            await throwGrain.Set(expected);
            await coordinator.MultiGrainSet(grains, expected);
            await Assert.ThrowsAsync<OrleansTransactionAbortedException>(() => coordinator.MultiGrainAddAndThrow(throwGrain, grains, expected));

            grains.Add(throwGrain);

            await TestAfterDustSettles(async () =>
            {
                foreach (var grain in grains)
                {
                    int[] actualValues = await grain.Get();
                    foreach (var actual in actualValues)
                    {
                        Assert.Equal(expected, actual);
                    }
                }
            });
        }

        [SkippableTheory(Skip = "Intermittent failure, jbragg investigating")]
        [InlineData(TransactionTestConstants.TransactionGrainStates.SingleStateTransaction)]
        public async Task AbortTransactionOnOrphanCalls(TransactionTestConstants.TransactionGrainStates grainStates)
        {
            const int expected = 5;

            ITransactionTestGrain grain = RandomTestGrain(grainStates);
            ITransactionCoordinatorGrain coordinator = this.grainFactory.GetGrain<ITransactionCoordinatorGrain>(Guid.NewGuid());

            await grain.Set(expected);
            await Assert.ThrowsAsync<OrleansOrphanCallException>(() => coordinator.OrphanCallTransaction(grain));

            //await Task.Delay(20000); // give time for GC

            await TestAfterDustSettles(async () =>
            {
                int[] actualValues = await grain.Get();
                foreach (var actual in actualValues)
                {
                    Assert.Equal(expected, actual);
                }
            });
        }

        private async Task TestAfterDustSettles(Func<Task> what)
        {
            int tries = 2;
            while (tries-- > 0)
            {
                try
                {
                    await what();
                }
                catch (OrleansCascadingAbortException)
                {
                    // due to optimistic reading we may read state of aborted transactions
                    // which causes cascading abort
                }
            }
        }
    }
}
