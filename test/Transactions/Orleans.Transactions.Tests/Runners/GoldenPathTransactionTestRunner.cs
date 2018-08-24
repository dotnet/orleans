using Orleans.Transactions.Tests.Consistency;
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


        // settings that are configuration dependent can be overridden by runner subclasses
        // this allows tests to adapt their logic, or be skipped, for specific contexts
        protected virtual bool StorageAdaptorHasLimitedCommitSpace => false;



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

        [SkippableTheory]
        // high congestion
        [InlineData(2, 2, true, true, ReadWriteDetermination.PerGrain)]
        [InlineData(2, 3, true, true, ReadWriteDetermination.PerGrain)]
        [InlineData(2, 4, true, true, ReadWriteDetermination.PerGrain)]
        [InlineData(2, 5, true, true, ReadWriteDetermination.PerGrain)]
        [InlineData(2, 2, true, true, ReadWriteDetermination.PerTransaction)]
        [InlineData(2, 3, true, true, ReadWriteDetermination.PerTransaction)]
        [InlineData(2, 4, true, true, ReadWriteDetermination.PerTransaction)]
        [InlineData(2, 5, true, true, ReadWriteDetermination.PerTransaction)]
        [InlineData(2, 2, true, true, ReadWriteDetermination.PerAccess)]
        [InlineData(2, 3, true, true, ReadWriteDetermination.PerAccess)]
        [InlineData(2, 4, true, true, ReadWriteDetermination.PerAccess)]
        [InlineData(2, 5, true, true, ReadWriteDetermination.PerAccess)]
        [InlineData(2, 2, false, true, ReadWriteDetermination.PerAccess)]
        [InlineData(2, 3, false, true, ReadWriteDetermination.PerAccess)]
        [InlineData(2, 4, false, true, ReadWriteDetermination.PerAccess)]
        [InlineData(2, 5, false, true, ReadWriteDetermination.PerAccess)]
        [InlineData(2, 2, true, false, ReadWriteDetermination.PerGrain)]
        [InlineData(2, 3, true, false, ReadWriteDetermination.PerGrain)]
        [InlineData(2, 4, true, false, ReadWriteDetermination.PerGrain)]
        [InlineData(2, 5, true, false, ReadWriteDetermination.PerGrain)]
        [InlineData(2, 2, true, false, ReadWriteDetermination.PerTransaction)]
        [InlineData(2, 3, true, false, ReadWriteDetermination.PerTransaction)]
        [InlineData(2, 4, true, false, ReadWriteDetermination.PerTransaction)]
        [InlineData(2, 5, true, false, ReadWriteDetermination.PerTransaction)]
        [InlineData(2, 2, true, false, ReadWriteDetermination.PerAccess)]
        [InlineData(2, 3, true, false, ReadWriteDetermination.PerAccess)]
        [InlineData(2, 4, true, false, ReadWriteDetermination.PerAccess)]
        [InlineData(2, 5, true, false, ReadWriteDetermination.PerAccess)]
        [InlineData(2, 2, false, false, ReadWriteDetermination.PerAccess)]
        [InlineData(2, 3, false, false, ReadWriteDetermination.PerAccess)]
        [InlineData(2, 4, false, false, ReadWriteDetermination.PerAccess)]
        [InlineData(2, 5, false, false, ReadWriteDetermination.PerAccess)]
        // medium congestion
        [InlineData(30, 2, true, true, ReadWriteDetermination.PerGrain)]
        [InlineData(30, 3, true, true, ReadWriteDetermination.PerGrain)]
        [InlineData(30, 4, true, true, ReadWriteDetermination.PerGrain)]
        [InlineData(30, 2, true, true, ReadWriteDetermination.PerTransaction)]
        [InlineData(30, 3, true, true, ReadWriteDetermination.PerTransaction)]
        [InlineData(30, 4, true, true, ReadWriteDetermination.PerTransaction)]
        [InlineData(30, 2, true, true, ReadWriteDetermination.PerAccess)]
        [InlineData(30, 3, true, true, ReadWriteDetermination.PerAccess)]
        [InlineData(30, 4, true, true, ReadWriteDetermination.PerAccess)]
        [InlineData(30, 2, false, true, ReadWriteDetermination.PerAccess)]
        [InlineData(30, 3, false, true, ReadWriteDetermination.PerAccess)]
        [InlineData(30, 4, false, true, ReadWriteDetermination.PerAccess)]
        [InlineData(30, 2, true, false, ReadWriteDetermination.PerGrain)]
        [InlineData(30, 3, true, false, ReadWriteDetermination.PerGrain)]
        [InlineData(30, 4, true, false, ReadWriteDetermination.PerGrain)]
        [InlineData(30, 5, true, false, ReadWriteDetermination.PerGrain)]
        [InlineData(30, 2, true, false, ReadWriteDetermination.PerTransaction)]
        [InlineData(30, 3, true, false, ReadWriteDetermination.PerTransaction)]
        [InlineData(30, 4, true, false, ReadWriteDetermination.PerTransaction)]
        [InlineData(30, 5, true, false, ReadWriteDetermination.PerTransaction)]
        [InlineData(30, 2, true, false, ReadWriteDetermination.PerAccess)]
        [InlineData(30, 3, true, false, ReadWriteDetermination.PerAccess)]
        [InlineData(30, 4, true, false, ReadWriteDetermination.PerAccess)]
        [InlineData(30, 5, true, false, ReadWriteDetermination.PerAccess)]
        [InlineData(30, 2, false, false, ReadWriteDetermination.PerAccess)]
        [InlineData(30, 3, false, false, ReadWriteDetermination.PerAccess)]
        [InlineData(30, 4, false, false, ReadWriteDetermination.PerAccess)]
        [InlineData(30, 5, false, false, ReadWriteDetermination.PerAccess)]
        // low congestion
        [InlineData(1000, 2, false, true, ReadWriteDetermination.PerGrain)]
        [InlineData(1000, 3, false, true, ReadWriteDetermination.PerGrain)]
        [InlineData(1000, 4, false, true, ReadWriteDetermination.PerGrain)]
        [InlineData(1000, 2, false, true, ReadWriteDetermination.PerTransaction)]
        [InlineData(1000, 3, false, true, ReadWriteDetermination.PerTransaction)]
        [InlineData(1000, 4, false, true, ReadWriteDetermination.PerTransaction)]
        [InlineData(1000, 2, false, true, ReadWriteDetermination.PerAccess)]
        [InlineData(1000, 3, false, true, ReadWriteDetermination.PerAccess)]
        [InlineData(1000, 4, false, true, ReadWriteDetermination.PerAccess)]
        [InlineData(1000, 2, false, false, ReadWriteDetermination.PerGrain)]
        [InlineData(1000, 3, false, false, ReadWriteDetermination.PerGrain)]
        [InlineData(1000, 4, false, false, ReadWriteDetermination.PerGrain)]
        [InlineData(1000, 5, false, false, ReadWriteDetermination.PerGrain)]
        [InlineData(1000, 2, false, false, ReadWriteDetermination.PerTransaction)]
        [InlineData(1000, 3, false, false, ReadWriteDetermination.PerTransaction)]
        [InlineData(1000, 4, false, false, ReadWriteDetermination.PerTransaction)]
        [InlineData(1000, 5, false, false, ReadWriteDetermination.PerTransaction)]
        [InlineData(1000, 2, false, false, ReadWriteDetermination.PerAccess)]
        [InlineData(1000, 3, false, false, ReadWriteDetermination.PerAccess)]
        [InlineData(1000, 4, false, false, ReadWriteDetermination.PerAccess)]
        [InlineData(1000, 5, false, false, ReadWriteDetermination.PerAccess)]

        public virtual async Task RandomizedConsistency(int numGrains, int scale, bool avoidDeadlocks, bool avoidTimeouts, ReadWriteDetermination readwrite)
        {
            var random = new Random(scale + numGrains * 1000 + (avoidDeadlocks ? 666 : 333) + ((int)readwrite) * 123976);

            var harness = new ConsistencyTestHarness(grainFactory, numGrains, random.Next(), avoidDeadlocks, avoidTimeouts, readwrite, false);

            // first, run the random work load to generate history events
            output.WriteLine($"start at {DateTime.UtcNow}");
            int numThreads = scale;
            int numTxsPerThread = scale * scale;
            var tasks = new Task[numThreads];
            for (int i = 0; i < numThreads; i++)
            {
                tasks[i] = harness.RunRandomTransactionSequence(i, numTxsPerThread, grainFactory, this.output);
            }
            await Task.WhenAll(tasks);
            output.WriteLine($"end at {DateTime.UtcNow}");

            // all transactions are expected to pass when avoiding deadlocks and lock upgrades
            if (avoidDeadlocks && (readwrite == ReadWriteDetermination.PerGrain || readwrite == ReadWriteDetermination.PerTransaction))
            {
                Assert.Equal(0, harness.NumAborted);
            }

            // then, analyze the history results
            harness.CheckConsistency(tolerateGenericTimeouts: (scale >= 3 && !avoidTimeouts), tolerateUnknownExceptions: StorageAdaptorHasLimitedCommitSpace);
        }
    }
}
