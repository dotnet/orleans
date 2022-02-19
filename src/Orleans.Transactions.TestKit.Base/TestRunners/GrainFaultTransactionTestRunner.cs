using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;

namespace Orleans.Transactions.TestKit
{
    public abstract class GrainFaultTransactionTestRunner : TransactionTestRunnerBase
    {
        public GrainFaultTransactionTestRunner(IGrainFactory grainFactory, Action<string> output)
        : base(grainFactory, output)
        { }

        public virtual async Task AbortTransactionOnExceptions(string grainStates)
        {
            const int expected = 5;

            ITransactionTestGrain grain = RandomTestGrain(grainStates);
            ITransactionCoordinatorGrain coordinator = this.grainFactory.GetGrain<ITransactionCoordinatorGrain>(Guid.NewGuid());

            await coordinator.MultiGrainSet(new List<ITransactionTestGrain> { grain }, expected);
            Func<Task> task = () => coordinator.AddAndThrow(grain, expected);
            await task.Should().ThrowAsync<OrleansTransactionAbortedException>();

            await TestAfterDustSettles(async () =>
            {
                int[] actualValues = await grain.Get();
                foreach (var actual in actualValues)
                {
                    actual.Should().Be(expected);
                }
            });
        }

        public virtual async Task MultiGrainAbortTransactionOnExceptions(string grainStates)
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
            Func<Task> task = () => coordinator.MultiGrainAddAndThrow(new List<ITransactionTestGrain>()
            {
                throwGrain
            }, grains, expected);
            await task.Should().ThrowAsync<OrleansTransactionAbortedException>();
            grains.Add(throwGrain);

            await TestAfterDustSettles(async () =>
            {
                foreach (var grain in grains)
                {
                    int[] actualValues = await grain.Get();
                    foreach (var actual in actualValues)
                    {
                        actual.Should().Be(expected);
                    }
                }
            });
        }

        public virtual async Task AbortTransactionExceptionInnerExceptionOnlyContainsOneRootCauseException(string grainStates)
        {
            const int throwGrainCount = 3;
            const int grainCount = TransactionTestConstants.MaxCoordinatedTransactions - throwGrainCount;
            const int expected = 5;

            List<ITransactionTestGrain> throwGrains = Enumerable.Range(0, throwGrainCount)
                .Select(i => RandomTestGrain(grainStates))
                .ToList();
            List<ITransactionTestGrain> grains =
                Enumerable.Range(0, grainCount)
                    .Select(i => RandomTestGrain(grainStates))
                    .ToList();
            ITransactionCoordinatorGrain coordinator = this.grainFactory.GetGrain<ITransactionCoordinatorGrain>(Guid.NewGuid());

            await coordinator.MultiGrainSet(throwGrains, expected);
            await coordinator.MultiGrainSet(grains, expected);

            async Task InnerExceptionCheck()
            {
                try
                {
                    await coordinator.MultiGrainAddAndThrow(throwGrains, grains, expected);
                }
                catch (Exception e)
                {
                    e.InnerException.Should().BeOfType<AddAndThrowException>();
                    throw;
                }
            }

            Func<Task> task = () => InnerExceptionCheck();
            await task.Should().ThrowAsync<OrleansTransactionAbortedException>();

            grains.AddRange(throwGrains);

            await TestAfterDustSettles(async () =>
            {
                foreach (var grain in grains)
                {
                    int[] actualValues = await grain.Get();
                    foreach (var actual in actualValues)
                    {
                        actual.Should().Be(expected);
                    }
                }
            });
        }

        public virtual async Task AbortTransactionOnOrphanCalls(string grainStates)
        {
            const int expected = 5;

            ITransactionTestGrain grain = RandomTestGrain(grainStates);
            ITransactionCoordinatorGrain coordinator = this.grainFactory.GetGrain<ITransactionCoordinatorGrain>(Guid.NewGuid());

            await grain.Set(expected);
            Func<Task> task = () => coordinator.OrphanCallTransaction(grain);
            await task.Should().ThrowAsync<OrleansOrphanCallException>();

            //await Task.Delay(20000); // give time for GC

            await TestAfterDustSettles(async () =>
            {
                int[] actualValues = await grain.Get();
                foreach (var actual in actualValues)
                {
                    actual.Should().Be(expected);
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
