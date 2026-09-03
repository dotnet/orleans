
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;

namespace Orleans.Transactions.TestKit
{
    /// <summary>
    /// Runs transaction commit service scenarios which fail or throw while committing.
    /// </summary>
    public abstract class TocFaultTransactionTestRunner : TransactionTestRunnerBase
    {
        private static readonly TimeSpan VerificationRetryTimeout = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan VerificationRetryDelay = TimeSpan.FromMilliseconds(100);

        /// <summary>
        /// Initializes a new instance of the <see cref="TocFaultTransactionTestRunner"/> class.
        /// </summary>
        /// <param name="grainFactory">The grain factory used to access test grains.</param>
        /// <param name="output">The callback used to write test output.</param>
        protected TocFaultTransactionTestRunner(IGrainFactory grainFactory, Action<string> output)
        : base(grainFactory, output) { }

        /// <summary>
        /// Verifies that a commit service failure aborts a multi-grain transaction and preserves the prior committed state.
        /// </summary>
        /// <param name="grainStates">The transaction state configuration used to select test grains.</param>
        /// <param name="grainCount">The number of grains participating in the transaction.</param>
        /// <returns>A task which represents the test.</returns>
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

        /// <summary>
        /// Verifies that a commit service exception reports an in-doubt outcome while preserving the prior committed state.
        /// </summary>
        /// <param name="grainStates">The transaction state configuration used to select test grains.</param>
        /// <param name="grainCount">The number of grains participating in the transaction.</param>
        /// <returns>A task which represents the test.</returns>
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
