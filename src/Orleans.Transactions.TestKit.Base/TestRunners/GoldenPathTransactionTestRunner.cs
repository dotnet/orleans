using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;

namespace Orleans.Transactions.TestKit
{
    public abstract class GoldenPathTransactionTestRunner : TransactionTestRunnerBase
    {
        protected GoldenPathTransactionTestRunner(IGrainFactory grainFactory, Action<string> output)
        : base(grainFactory, output) { }

        public virtual async Task SingleGrainReadTransaction(string grainStates)
        {
            const int expected = 0;

            var grain = RandomTestGrain(grainStates);
            var actualResults = await grain.Get();
            //each transaction state should all be 0 since no operation was applied yet
            foreach (var actual in actualResults)
            {
                actual.Should().Be(expected);
            }
        }

        public virtual async Task SingleGrainWriteTransaction(string grainStates)
        {
            const int delta = 5;
            var grain = RandomTestGrain(grainStates);
            var original = await grain.Get();
            await grain.Add(delta);
            var expected = original.Select(value => value + delta).ToArray();
            var actual = await grain.Get();
            actual.Should().BeEquivalentTo(expected);
        }

        public virtual async Task MultiGrainWriteTransaction(string grainStates, int grainCount)
        {
            const int expected = 5;

            var grains =
                Enumerable.Range(0, grainCount)
                    .Select(i => RandomTestGrain(grainStates))
                    .ToList();

            var coordinator = grainFactory.GetGrain<ITransactionCoordinatorGrain>(Guid.NewGuid());

            await coordinator.MultiGrainAdd(grains, expected);

            foreach (var grain in grains)
            {
                var actualValues = await grain.Get();
                foreach (var actual in actualValues)
                {
                    actual.Should().Be(expected);
                }
            }
        }

        public virtual async Task MultiGrainReadWriteTransaction(string grainStates, int grainCount)
        {
            const int delta = 5;

            var grains =
                Enumerable.Range(0, grainCount)
                    .Select(i => RandomTestGrain(grainStates))
                    .ToList();

            var coordinator = grainFactory.GetGrain<ITransactionCoordinatorGrain>(Guid.NewGuid());

            await coordinator.MultiGrainSet(grains, delta);
            await coordinator.MultiGrainDouble(grains);

            var expected = delta + delta;
            foreach (var grain in grains)
            {
                var actualValues = await grain.Get();
                foreach (var actual in actualValues)
                {
                    if (expected != actual) testOutput($"{grain} - failed");
                    actual.Should().Be(expected);
                }
            }
        }

        public virtual async Task RepeatGrainReadWriteTransaction(string grainStates, int grainCount)
        {
            const int repeat = 10;
            const int delta = 5;

            var grainIds = Enumerable.Range(0, grainCount)
                    .Select(i => Guid.NewGuid())
                    .ToList();

            var grains = grainIds
                    .Select(id => TestGrain(grainStates, id))
                    .ToList();

            var coordinator = grainFactory.GetGrain<ITransactionCoordinatorGrain>(Guid.NewGuid());

            await coordinator.MultiGrainSet(grains, delta);
            for (var i = 0; i < repeat; i++)
            {
                await coordinator.MultiGrainDouble(grains);

                var expected = delta * (int)Math.Pow(2,i+1);
                foreach (var grain in grains)
                {
                    var actualValues = await grain.Get();
                    foreach (var actual in actualValues)
                    {
                        if (expected != actual) testOutput($"{grain} - failed");
                        actual.Should().Be(expected);
                    }
                }
            }
        }

        public virtual async Task MultiWriteToSingleGrainTransaction(string grainStates)
        {
            const int delta = 5;
            const int concurrentWrites = 3;

            var grain = RandomTestGrain(grainStates);
            var grains = Enumerable.Repeat(grain, concurrentWrites).ToList();

            var coordinator = grainFactory.GetGrain<ITransactionCoordinatorGrain>(Guid.NewGuid());

            await coordinator.MultiGrainAdd(grains, delta);

            var expected = delta * concurrentWrites;
            var actualValues = await grains[0].Get();
            foreach (var actual in actualValues)
            {
                actual.Should().Be(expected);
            }
        }

        public virtual async Task RWRWTest(string grainStates, int grainCount)
        {
            const int delta = 5;

            var grains =
                Enumerable.Range(0, grainCount)
                    .Select(i => RandomTestGrain(grainStates))
                    .ToList();

            var coordinator = grainFactory.GetGrain<ITransactionCoordinatorGrain>(Guid.NewGuid());

            await coordinator.MultiGrainDoubleByRWRW(grains, delta);

            var expected = delta + delta;
            foreach (var grain in grains)
            {
                var actualValues = await grain.Get();
                foreach (var actual in actualValues)
                {
                    if (expected != actual) testOutput($"{grain} - failed");
                    actual.Should().Be(expected);
                }
            }
        }

        public virtual async Task WRWRTest(string grainStates, int grainCount)
        {
            const int delta = 5;

            var grains =
                Enumerable.Range(0, grainCount)
                    .Select(i => RandomTestGrain(grainStates))
                    .ToList();

            var coordinator = grainFactory.GetGrain<ITransactionCoordinatorGrain>(Guid.NewGuid());

            await coordinator.MultiGrainDoubleByWRWR(grains, delta);

            var expected = delta + delta;
            foreach (var grain in grains)
            {
                var actualValues = await grain.Get();
                foreach (var actual in actualValues)
                {
                    if (expected != actual) testOutput($"{grain} - failed");
                    actual.Should().Be(expected);
                }
            }
        }

    }
}
