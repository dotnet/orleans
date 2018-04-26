using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace Orleans.Transactions.Tests
{
    public abstract class GoldenPathTransactionTestRunner : TransactionTestRunnerBase
    {
        protected GoldenPathTransactionTestRunner(IGrainFactory grainFactory, ITestOutputHelper output, bool distributedTm = false)
        : base(grainFactory, output, distributedTm) { }

        [SkippableTheory]
        [InlineData(TransactionTestConstants.TransactionGrainStates.SingleStateTransaction)]
        [InlineData(TransactionTestConstants.TransactionGrainStates.DoubleStateTransaction)]
        [InlineData(TransactionTestConstants.TransactionGrainStates.MaxStateTransaction)]
        public virtual async Task SingleGrainReadTransaction(TransactionTestConstants.TransactionGrainStates grainStates)
        {
            const int expected = 0;

            ITransactionTestGrain grain = RandomTestGrain(grainStates);
            var actualResults = await grain.Get();
            //each transaction state should all be 0 since no operation was applied yet
            foreach (var actual in actualResults)
            {
                Assert.Equal(expected, actual);
            }
        }

        [SkippableTheory]
        [InlineData(TransactionTestConstants.TransactionGrainStates.SingleStateTransaction)]
        [InlineData(TransactionTestConstants.TransactionGrainStates.DoubleStateTransaction)]
        [InlineData(TransactionTestConstants.TransactionGrainStates.MaxStateTransaction)]
        public virtual async Task SingleGrainWriteTransaction(TransactionTestConstants.TransactionGrainStates grainStates)
        {
            const int delta = 5;
            ITransactionTestGrain grain = RandomTestGrain(grainStates);
            var original = await grain.Get();
            await grain.Add(delta);
            var expected = original.Select(value => value + delta).ToArray();
            var actual = await grain.Get();
            Assert.Equal(expected, actual);
        }

        [SkippableTheory]
        [InlineData(TransactionTestConstants.TransactionGrainStates.SingleStateTransaction)]
        [InlineData(TransactionTestConstants.TransactionGrainStates.DoubleStateTransaction)]
        [InlineData(TransactionTestConstants.TransactionGrainStates.MaxStateTransaction)]
        public virtual async Task MultiGrainWriteTransaction(TransactionTestConstants.TransactionGrainStates grainStates)
        {
            const int expected = 5;
            const int grainCount = TransactionTestConstants.MaxCoordinatedTransactions;

            List<ITransactionTestGrain> grains =
                Enumerable.Range(0, grainCount)
                    .Select(i => RandomTestGrain(grainStates))
                    .ToList();

            ITransactionCoordinatorGrain coordinator = this.grainFactory.GetGrain<ITransactionCoordinatorGrain>(Guid.NewGuid());

            await coordinator.MultiGrainAdd(grains, expected);

            foreach (var grain in grains)
            {
                var actualValues = await grain.Get();
                foreach (var actual in actualValues)
                {
                    Assert.Equal(expected, actual);
                }
            }
        }

        [SkippableTheory]
        [InlineData(TransactionTestConstants.TransactionGrainStates.SingleStateTransaction)]
        [InlineData(TransactionTestConstants.TransactionGrainStates.DoubleStateTransaction)]
        [InlineData(TransactionTestConstants.TransactionGrainStates.MaxStateTransaction)]
        public virtual async Task MultiGrainReadWriteTransaction(TransactionTestConstants.TransactionGrainStates grainStates)
        {
            const int delta = 5;
            const int grainCount = TransactionTestConstants.MaxCoordinatedTransactions;

            List<ITransactionTestGrain> grains =
                Enumerable.Range(0, grainCount)
                    .Select(i => RandomTestGrain(grainStates))
                    .ToList();

            ITransactionCoordinatorGrain coordinator = this.grainFactory.GetGrain<ITransactionCoordinatorGrain>(Guid.NewGuid());

            await coordinator.MultiGrainSet(grains, delta);
            await coordinator.MultiGrainDouble(grains);

            int expected = delta + delta;
            foreach (var grain in grains)
            {
                int[] actualValues = await grain.Get();
                foreach (var actual in actualValues)
                {
                    if (expected != actual) this.output.WriteLine($"{grain} - failed");
                    Assert.Equal(expected, actual);
                }
            }
        }

        [SkippableTheory]
        [InlineData(TransactionTestConstants.TransactionGrainStates.SingleStateTransaction)]
        [InlineData(TransactionTestConstants.TransactionGrainStates.DoubleStateTransaction)]
        [InlineData(TransactionTestConstants.TransactionGrainStates.MaxStateTransaction)]
        public virtual async Task RepeatGrainReadWriteTransaction(TransactionTestConstants.TransactionGrainStates grainStates)
        {
            const int repeat = 10;
            const int delta = 5;
            const int grainCount = TransactionTestConstants.MaxCoordinatedTransactions;

            List<Guid> grainIds = Enumerable.Range(0, grainCount)
                    .Select(i => Guid.NewGuid())
                    .ToList();

            List<ITransactionTestGrain> grains = grainIds
                    .Select(id => TestGrain(grainStates, id))
                    .ToList();

            ITransactionCoordinatorGrain coordinator = this.grainFactory.GetGrain<ITransactionCoordinatorGrain>(Guid.NewGuid());

            await coordinator.MultiGrainSet(grains, delta);
            for (int i = 0; i < repeat; i++)
            {
                await coordinator.MultiGrainDouble(grains);

                int expected = delta * (int)Math.Pow(2,i+1);
                foreach (var grain in grains)
                {
                    int[] actualValues = await grain.Get();
                    foreach (var actual in actualValues)
                    {
                        if (expected != actual) this.output.WriteLine($"{grain} - failed");
                        Assert.Equal(expected, actual);
                    }
                }
            }
        }

        [SkippableTheory]
        [InlineData(TransactionTestConstants.TransactionGrainStates.SingleStateTransaction)]
        [InlineData(TransactionTestConstants.TransactionGrainStates.DoubleStateTransaction)]
        [InlineData(TransactionTestConstants.TransactionGrainStates.MaxStateTransaction)]
        public virtual async Task MultiWriteToSingleGrainTransaction(TransactionTestConstants.TransactionGrainStates grainStates)
        {
            const int delta = 5;
            const int concurrentWrites = 3;

            ITransactionTestGrain grain = RandomTestGrain(grainStates);
            List<ITransactionTestGrain> grains = Enumerable.Repeat(grain, concurrentWrites).ToList();

            ITransactionCoordinatorGrain coordinator = this.grainFactory.GetGrain<ITransactionCoordinatorGrain>(Guid.NewGuid());

            await coordinator.MultiGrainAdd(grains, delta);

            int expected = delta * concurrentWrites;
            int[] actualValues = await grains[0].Get();
            foreach (var actual in actualValues)
            {
                Assert.Equal(expected, actual);
            }
        }
    }
}
