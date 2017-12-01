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
        private readonly TimeSpan DefaultWaitTime = TimeSpan.FromSeconds(30);
        private readonly TimeSpan waitTime;
        protected OrchestrationsTransactionsTestRunner(IGrainFactory grainFactory, ITestOutputHelper output, TimeSpan? waitTime = null)
        : base(grainFactory, output)
        {
            this.waitTime = waitTime ?? DefaultWaitTime;
        }

        [SkippableTheory]
        [InlineData(TransactionTestConstants.TransactionOrchestrationGrain)]
        public virtual async Task SingleGrainReadTransaction(string transactionTestGrainClassName)
        {
            Guid grainId = Guid.NewGuid();
            ITransactionTestGrain grain = TestGrain(transactionTestGrainClassName, grainId);
            await grain.Get();
            await CheckReport(grainId, 1, 0);
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
            await CheckReport(grainId, 3, 0);
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
                await CheckReport(grainId, 2, 0);
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
                await CheckReport(grainId, 3, 0);
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
            await CheckReport(grainId, 2, 0);
        }

        private async Task CheckReport(Guid grainId, int perpareCount, int abortCount)
        {
            var endTime = DateTime.UtcNow + this.waitTime;
            while (DateTime.UtcNow < endTime)
            {
                bool passed = await CheckReport(grainId, perpareCount, abortCount, false);
                if (passed) return;
                await Task.Delay(this.waitTime.Milliseconds / 10);
            }
            await CheckReport(grainId, perpareCount, abortCount, true);
        }

        private async Task<bool> CheckReport(Guid grainId, int perpareCount, int abortCount, bool assert)
        {
            var resultGrain = this.grainFactory.GetGrain<ITransactionOrchestrationResultGrain>(grainId);

            TransactionOrchestrationResult results = await resultGrain.GetResults();

            if(assert)
            {
                Assert.Equal(perpareCount, results.Prepared.Count);
                Assert.Equal(abortCount, results.Aborted.Count);
                Assert.Equal(results.Prepared.Max(), results.Committed.Aggregate((long)0, (t1, t2) => Math.Max(t1, t2)));
            }
            else if(perpareCount != results.Prepared.Count ||
                      abortCount != results.Aborted.Count ||
                      results.Prepared.Max() != results.Committed.Aggregate((long)0, (t1, t2) => Math.Max(t1, t2)))
            {
                return false;
            }
            return true;
        }
    }
}
