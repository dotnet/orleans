
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;

namespace Orleans.Transactions.TestKit
{
    public abstract class TocFaultTransactionTestRunner : TransactionTestRunnerBase
    {
        private static readonly TimeSpan VerificationRetryTimeout = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan VerificationRetryDelay = TimeSpan.FromMilliseconds(100);

        protected TocFaultTransactionTestRunner(IGrainFactory grainFactory, Action<string> output)
        : base(grainFactory, output) { }

        public virtual async Task MultiGrainWriteTransactionWithCommitFailure(string grainStates, int grainCount)
        {
            const int expected = 5;

            ITransactionCommitterTestGrain committer = this.grainFactory.GetGrain<ITransactionCommitterTestGrain>(Guid.NewGuid());
            List<ITransactionTestGrain> grains =
                Enumerable.Range(0, grainCount)
                    .Select(i => RandomTestGrain(grainStates))
                    .ToList();

            ITransactionCoordinatorGrain coordinator = this.grainFactory.GetGrain<ITransactionCoordinatorGrain>(Guid.NewGuid());

            await coordinator.MultiGrainAdd(committer, new PassOperation("pass"), grains, expected);

            Func<Task> task = () => coordinator.MultiGrainAdd(committer, new FailOperation("fail"), grains, expected);
            await task.Should().ThrowAsync<OrleansTransactionAbortedException>();

            foreach (var grain in grains)
            {
                var actualValues = await GetWithTransientFailureRetry(grain);
                foreach (var actual in actualValues)
                {
                    actual.Should().Be(expected);
                }
            }

            // TODO : Add verification that commit service receive call with proper args.
        }

        public virtual async Task MultiGrainWriteTransactionWithCommitException(string grainStates, int grainCount)
        {
            const int expected = 5;

            ITransactionCommitterTestGrain committer = this.grainFactory.GetGrain<ITransactionCommitterTestGrain>(Guid.NewGuid());
            List<ITransactionTestGrain> grains =
                Enumerable.Range(0, grainCount)
                    .Select(i => RandomTestGrain(grainStates))
                    .ToList();

            ITransactionCoordinatorGrain coordinator = this.grainFactory.GetGrain<ITransactionCoordinatorGrain>(Guid.NewGuid());

            await coordinator.MultiGrainAdd(committer, new PassOperation("pass"), grains, expected);

            Func<Task> task = () => coordinator.MultiGrainAdd(committer, new ThrowOperation("throw"), grains, expected);
            await task.Should().ThrowAsync<OrleansTransactionInDoubtException>();

            foreach (var grain in grains)
            {
                var actualValues = await GetWithTransientFailureRetry(grain);
                foreach (var actual in actualValues)
                {
                    actual.Should().Be(expected);
                }
            }

            // TODO : Add verification that commit service receive call with proper args.
        }

        private async Task<int[]> GetWithTransientFailureRetry(ITransactionTestGrain grain)
        {
            var retryStart = Stopwatch.GetTimestamp();

            while (true)
            {
                try
                {
                    return await grain.Get();
                }
                // The commit result can arrive before cancellation reaches every participant. A verification read
                // which overlaps that cleanup is intentionally aborted and is safe to retry.
                catch (OrleansTransactionTransientFailureException exception)
                    when (Stopwatch.GetElapsedTime(retryStart) < VerificationRetryTimeout)
                {
                    this.testOutput($"State verification failed transiently: {exception.Message}. Retrying.");
                    await Task.Delay(VerificationRetryDelay);
                }
            }
        }
    }
}
