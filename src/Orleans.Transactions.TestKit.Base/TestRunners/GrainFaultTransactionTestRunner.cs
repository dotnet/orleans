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

            var grain = RandomTestGrain(grainStates);
            var coordinator = this.grainFactory.GetGrain<ITransactionCoordinatorGrain>(Guid.NewGuid());

            await coordinator.MultiGrainSet(new List<ITransactionTestGrain> { grain }, expected);
            Func<Task> task = () => coordinator.AddAndThrow(grain, expected);
            await task.Should().ThrowAsync<OrleansTransactionAbortedException>();

            await TestAfterDustSettles(async () =>
            {
                var actualValues = await grain.Get();
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

            var throwGrain = RandomTestGrain(grainStates);
            var grains =
                Enumerable.Range(0, grainCount)
                    .Select(i => RandomTestGrain(grainStates))
                    .ToList();
            var coordinator = this.grainFactory.GetGrain<ITransactionCoordinatorGrain>(Guid.NewGuid());

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
                    var actualValues = await grain.Get();
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

            var throwGrains = Enumerable.Range(0, throwGrainCount)
                .Select(i => RandomTestGrain(grainStates))
                .ToList();
            var grains =
                Enumerable.Range(0, grainCount)
                    .Select(i => RandomTestGrain(grainStates))
                    .ToList();
            var coordinator = this.grainFactory.GetGrain<ITransactionCoordinatorGrain>(Guid.NewGuid());

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
                    var actualValues = await grain.Get();
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

            var grain = RandomTestGrain(grainStates);
            var coordinator = this.grainFactory.GetGrain<ITransactionCoordinatorGrain>(Guid.NewGuid());

            await grain.Set(expected);
            Func<Task> task = () => coordinator.OrphanCallTransaction(grain);
            await task.Should().ThrowAsync<OrleansOrphanCallException>();

            //await Task.Delay(20000); // give time for GC

            await TestAfterDustSettles(async () =>
            {
                var actualValues = await grain.Get();
                foreach (var actual in actualValues)
                {
                    actual.Should().Be(expected);
                }
            });
        }

        private async Task TestAfterDustSettles(Func<Task> what)
        {
            var tries = 2;
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
