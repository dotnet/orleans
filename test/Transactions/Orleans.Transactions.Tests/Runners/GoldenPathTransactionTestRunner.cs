﻿using Orleans.Transactions.Tests.Consistency;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace Orleans.Transactions.Tests
{
    public abstract class GoldenPathTransactionTestRunner : TransactionTestRunnerBase
    {
        protected GoldenPathTransactionTestRunner(IGrainFactory grainFactory, ITestOutputHelper output)
        : base(grainFactory, output) { }


        [SkippableTheory]
        [InlineData(TransactionTestConstants.SingleStateTransactionalGrain)]
        [InlineData(TransactionTestConstants.DoubleStateTransactionalGrain)]
        [InlineData(TransactionTestConstants.MaxStateTransactionalGrain)]
        public virtual async Task SingleGrainReadTransaction(string grainStates)
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
        [InlineData(TransactionTestConstants.SingleStateTransactionalGrain)]
        [InlineData(TransactionTestConstants.DoubleStateTransactionalGrain)]
        [InlineData(TransactionTestConstants.MaxStateTransactionalGrain)]
        public virtual async Task SingleGrainWriteTransaction(string grainStates)
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
        [InlineData(TransactionTestConstants.SingleStateTransactionalGrain, TransactionTestConstants.MaxCoordinatedTransactions)]
        [InlineData(TransactionTestConstants.DoubleStateTransactionalGrain, TransactionTestConstants.MaxCoordinatedTransactions / 2)]
        [InlineData(TransactionTestConstants.MaxStateTransactionalGrain, 1)]
        public virtual async Task MultiGrainWriteTransaction(string grainStates, int grainCount)
        {
            const int expected = 5;

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
        [InlineData(TransactionTestConstants.SingleStateTransactionalGrain, TransactionTestConstants.MaxCoordinatedTransactions)]
        [InlineData(TransactionTestConstants.DoubleStateTransactionalGrain, TransactionTestConstants.MaxCoordinatedTransactions / 2)]
        [InlineData(TransactionTestConstants.MaxStateTransactionalGrain, 1)]
        public virtual async Task MultiGrainReadWriteTransaction(string grainStates, int grainCount)
        {
            const int delta = 5;

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
        [InlineData(TransactionTestConstants.SingleStateTransactionalGrain, TransactionTestConstants.MaxCoordinatedTransactions)]
        [InlineData(TransactionTestConstants.DoubleStateTransactionalGrain, TransactionTestConstants.MaxCoordinatedTransactions/2)]
        [InlineData(TransactionTestConstants.MaxStateTransactionalGrain, 1)]
        public virtual async Task RepeatGrainReadWriteTransaction(string grainStates, int grainCount)
        {
            const int repeat = 10;
            const int delta = 5;

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
        [InlineData(TransactionTestConstants.SingleStateTransactionalGrain)]
        [InlineData(TransactionTestConstants.DoubleStateTransactionalGrain)]
        [InlineData(TransactionTestConstants.MaxStateTransactionalGrain)]
        public virtual async Task MultiWriteToSingleGrainTransaction(string grainStates)
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
