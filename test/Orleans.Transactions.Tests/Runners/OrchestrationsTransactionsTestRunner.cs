using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;

namespace Orleans.Transactions.Tests
{
    public abstract class OrchestrationsTransactionsTestRunner : TransactionTestRunnerBase
    {
        protected OrchestrationsTransactionsTestRunner(IGrainFactory grainFactory, ITestOutputHelper output)
        : base(grainFactory, output) { }

        [SkippableTheory]
        [InlineData(TransactionTestConstants.TransactionOrchestrationGrain)]
        public virtual async Task SingleGrainReadTransaction(string transactionTestGrainClassName)
        {
            Guid grainId = Guid.NewGuid();
            ITransactionTestGrain grain = TestGrain(transactionTestGrainClassName, grainId);
            await grain.Get();
            await CheckReport(grainId, 1, 0, 1);
        }

        [SkippableTheory]
        [InlineData(TransactionTestConstants.TransactionOrchestrationGrain)]
        public virtual async Task SingleGrainWriteTransaction(string transactionTestGrainClassName)
        {
            const int delta = 5;
            Guid grainId = Guid.NewGuid();
            ITransactionTestGrain grain = TestGrain(transactionTestGrainClassName, grainId);
            await grain.Get();
            await grain.Add(delta);
            await grain.Get();
            await CheckReport(grainId, 3, 0, 3);
        }

        [SkippableTheory]
        [InlineData(TransactionTestConstants.TransactionOrchestrationGrain)]
        public virtual async Task MultiGrainWriteTransaction(string transactionTestGrainClassName)
        {
            const int expected = 5;
            const int grainCount = TransactionTestConstants.MaxCoordinatedTransactions;

            List<Guid> grainIds = Enumerable.Range(0, grainCount)
                .Select(i => Guid.NewGuid())
                .ToList();
            List<ITransactionTestGrain> grains = grainIds
                    .Select(id => TestGrain(transactionTestGrainClassName, id))
                    .ToList();

            ITransactionCoordinatorGrain coordinator = this.grainFactory.GetGrain<ITransactionCoordinatorGrain>(Guid.NewGuid());

            await coordinator.MultiGrainAdd(grains, expected);

            foreach (var grain in grains)
            {
                await grain.Get();
            }
            foreach (var grainId in grainIds)
            {
                await CheckReport(grainId, 2, 0, 2);
            }
        }

        [SkippableTheory]
        [InlineData(TransactionTestConstants.TransactionOrchestrationGrain)]
        public virtual async Task MultiGrainReadWriteTransaction(string transactionTestGrainClassName)
        {
            const int delta = 5;
            const int grainCount = TransactionTestConstants.MaxCoordinatedTransactions;

            List<Guid> grainIds = Enumerable.Range(0, grainCount)
                .Select(i => Guid.NewGuid())
                .ToList();
            List<ITransactionTestGrain> grains = grainIds
                    .Select(id => TestGrain(transactionTestGrainClassName, id))
                    .ToList();

            ITransactionCoordinatorGrain coordinator = this.grainFactory.GetGrain<ITransactionCoordinatorGrain>(Guid.NewGuid());

            await coordinator.MultiGrainSet(grains, delta);
            await coordinator.MultiGrainDouble(grains);

            foreach (var grain in grains)
            {
                await grain.Get();
            }
            foreach (var grainId in grainIds)
            {
                await CheckReport(grainId, 3, 0, 3);
            }
        }

        [SkippableTheory]
        [InlineData(TransactionTestConstants.TransactionOrchestrationGrain)]
        public virtual async Task MultiWriteToSingleGrainTransaction(string transactionTestGrainClassName)
        {
            const int delta = 5;
            const int concurrentWrites = TransactionTestConstants.MaxCoordinatedTransactions;

            Guid grainId = Guid.NewGuid();
            ITransactionTestGrain grain = TestGrain(transactionTestGrainClassName, grainId);
            List<ITransactionTestGrain> grains = Enumerable.Repeat(grain, concurrentWrites).ToList();

            ITransactionCoordinatorGrain coordinator = this.grainFactory.GetGrain<ITransactionCoordinatorGrain>(Guid.NewGuid());

            await coordinator.MultiGrainAdd(grains, delta);

            await grains[0].Get();
            await CheckReport(grainId, 2, 0, 2);
        }

        private async Task CheckReport(Guid grainId, int perpareCount, int abortCount, int commitCount)
        {
            var resultGrain = this.grainFactory.GetGrain<ITransactionOrchestrationResultGrain>(grainId);

            TransactionOrchestrationResult results = await resultGrain.GetResults();

            Assert.Equal(perpareCount, results.Prepared.Count);
            Assert.Equal(abortCount, results.Aborted.Count);
            Assert.Equal(commitCount, results.Committed.Count);
        }
    }
}
