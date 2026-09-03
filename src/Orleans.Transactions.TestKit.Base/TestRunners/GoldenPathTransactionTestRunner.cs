using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;

namespace Orleans.Transactions.TestKit
{
    /// <summary>
    /// Runs successful transaction scenarios across one or more grains.
    /// </summary>
    public abstract class GoldenPathTransactionTestRunner : TransactionTestRunnerBase
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="GoldenPathTransactionTestRunner"/> class.
        /// </summary>
        /// <param name="grainFactory">The grain factory used to access test grains.</param>
        /// <param name="output">The callback used to write test output.</param>
        protected GoldenPathTransactionTestRunner(IGrainFactory grainFactory, Action<string> output)
        : base(grainFactory, output) { }

        /// <summary>
        /// Verifies the initial state returned by a single-grain read transaction.
        /// </summary>
        /// <param name="grainStates">The transaction state configuration used to select the test grain.</param>
        /// <returns>A task which represents the test.</returns>
        public virtual async Task SingleGrainReadTransaction(string grainStates)
        {
            const int expected = 0;

            ITransactionTestGrain grain = RandomTestGrain(grainStates);
            var actualResults = await grain.Get();
            //each transaction state should all be 0 since no operation was applied yet
            foreach (var actual in actualResults)
            {
                actual.Should().Be(expected);
            }
        }

        /// <summary>
        /// Verifies a successful write transaction on a single grain.
        /// </summary>
        /// <param name="grainStates">The transaction state configuration used to select the test grain.</param>
        /// <returns>A task which represents the test.</returns>
        public virtual async Task SingleGrainWriteTransaction(string grainStates)
        {
            const int delta = 5;
            ITransactionTestGrain grain = RandomTestGrain(grainStates);
            var original = await grain.Get();
            await grain.Add(delta);
            var expected = original.Select(value => value + delta).ToArray();
            var actual = await grain.Get();
            actual.Should().BeEquivalentTo(expected);
        }

        /// <summary>
        /// Verifies a successful write transaction across multiple grains.
        /// </summary>
        /// <param name="grainStates">The transaction state configuration used to select test grains.</param>
        /// <param name="grainCount">The number of grains participating in the transaction.</param>
        /// <returns>A task which represents the test.</returns>
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
                    actual.Should().Be(expected);
                }
            }
        }

        /// <summary>
        /// Verifies successive set and read-modify-write operations across multiple grains.
        /// </summary>
        /// <param name="grainStates">The transaction state configuration used to select test grains.</param>
        /// <param name="grainCount">The number of grains participating in the transaction.</param>
        /// <returns>A task which represents the test.</returns>
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
                    if (expected != actual) this.testOutput($"{grain} - failed");
                    actual.Should().Be(expected);
                }
            }
        }

        /// <summary>
        /// Verifies repeated read-modify-write transactions across a fixed set of grains.
        /// </summary>
        /// <param name="grainStates">The transaction state configuration used to select test grains.</param>
        /// <param name="grainCount">The number of grains participating in each transaction.</param>
        /// <returns>A task which represents the test.</returns>
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
                        if (expected != actual) this.testOutput($"{grain} - failed");
                        actual.Should().Be(expected);
                    }
                }
            }
        }

        /// <summary>
        /// Verifies that multiple writes to the same grain within one transaction are all applied.
        /// </summary>
        /// <param name="grainStates">The transaction state configuration used to select the test grain.</param>
        /// <returns>A task which represents the test.</returns>
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
                actual.Should().Be(expected);
            }
        }

        /// <summary>
        /// Verifies a read-write-read-write access sequence across multiple grains.
        /// </summary>
        /// <param name="grainStates">The transaction state configuration used to select test grains.</param>
        /// <param name="grainCount">The number of grains participating in the transaction.</param>
        /// <returns>A task which represents the test.</returns>
        public virtual async Task RWRWTest(string grainStates, int grainCount)
        {
            const int delta = 5;

            List<ITransactionTestGrain> grains =
                Enumerable.Range(0, grainCount)
                    .Select(i => RandomTestGrain(grainStates))
                    .ToList();

            ITransactionCoordinatorGrain coordinator = this.grainFactory.GetGrain<ITransactionCoordinatorGrain>(Guid.NewGuid());

            await coordinator.MultiGrainDoubleByRWRW(grains, delta);

            int expected = delta + delta;
            foreach (var grain in grains)
            {
                int[] actualValues = await grain.Get();
                foreach (var actual in actualValues)
                {
                    if (expected != actual) this.testOutput($"{grain} - failed");
                    actual.Should().Be(expected);
                }
            }
        }

        /// <summary>
        /// Verifies a write-read-write-read access sequence across multiple grains.
        /// </summary>
        /// <param name="grainStates">The transaction state configuration used to select test grains.</param>
        /// <param name="grainCount">The number of grains participating in the transaction.</param>
        /// <returns>A task which represents the test.</returns>
        public virtual async Task WRWRTest(string grainStates, int grainCount)
        {
            const int delta = 5;

            List<ITransactionTestGrain> grains =
                Enumerable.Range(0, grainCount)
                    .Select(i => RandomTestGrain(grainStates))
                    .ToList();

            ITransactionCoordinatorGrain coordinator = this.grainFactory.GetGrain<ITransactionCoordinatorGrain>(Guid.NewGuid());

            await coordinator.MultiGrainDoubleByWRWR(grains, delta);

            int expected = delta + delta;
            foreach (var grain in grains)
            {
                int[] actualValues = await grain.Get();
                foreach (var actual in actualValues)
                {
                    if (expected != actual) this.testOutput($"{grain} - failed");
                    actual.Should().Be(expected);
                }
            }
        }

    }
}
