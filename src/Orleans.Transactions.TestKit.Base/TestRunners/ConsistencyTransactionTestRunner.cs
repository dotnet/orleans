using System;
using System.Threading.Tasks;
using AwesomeAssertions;
using Orleans.Transactions.TestKit.Consistency;

namespace Orleans.Transactions.TestKit
{
    public abstract class ConsistencyTransactionTestRunner : TransactionTestRunnerBase
    {
        protected ConsistencyTransactionTestRunner(IGrainFactory grainFactory, Action<string> output)
        : base(grainFactory, output) { }


        // settings that are configuration dependent can be overridden by runner subclasses
        // this allows tests to adapt their logic, or be skipped, for specific contexts
        protected abstract bool StorageAdaptorHasLimitedCommitSpace { get; }
        protected abstract bool StorageErrorInjectionActive { get; }

        public virtual async Task RandomizedConsistency(int numGrains, int scale, bool avoidDeadlocks, bool avoidTimeouts, ReadWriteDetermination readwrite)
        {
            var random = new Random(scale + numGrains * 1000 + (avoidDeadlocks ? 666 : 333) + ((int)readwrite) * 123976);

            var harness = new ConsistencyTestHarness(grainFactory, numGrains, random.Next(), avoidDeadlocks, avoidTimeouts, readwrite, StorageErrorInjectionActive);

            // first, run the random work load to generate history events
            testOutput($"start at {DateTime.UtcNow}");
            int numThreads = scale;
            int numTxsPerThread = scale * scale;

            // start the threads that run transactions
            var tasks = new Task[numThreads];
            for (int i = 0; i < numThreads; i++)
            {
                tasks[i] = harness.RunRandomTransactionSequence(i, numTxsPerThread, grainFactory, this.testOutput);
            }

            // wait for the test to finish
            await Task.WhenAll(tasks);
            testOutput($"end at {DateTime.UtcNow}");

            // golden path: all transactions are expected to pass when avoiding deadlocks and lock upgrades
            if (!StorageErrorInjectionActive
                && avoidDeadlocks
                && (readwrite == ReadWriteDetermination.PerGrain || readwrite == ReadWriteDetermination.PerTransaction))
            {
                harness.NumAborted.Should().Be(0);
            }

            // then, analyze the history results
            var tolerateGenericTimeouts = ShouldTolerateGenericTimeouts(scale, StorageErrorInjectionActive);
            var tolerateUnknownExceptions = StorageAdaptorHasLimitedCommitSpace || StorageErrorInjectionActive;
            harness.CheckConsistency(tolerateGenericTimeouts, tolerateUnknownExceptions);
        }

        internal static bool ShouldTolerateGenericTimeouts(int scale, bool storageErrorInjectionActive)
        {
            // AvoidTimeouts limits recursive work before the response timeout, but cannot prevent
            // the response timeout itself from expiring when the system is under load.
            return storageErrorInjectionActive || scale >= 3;
        }
    }
}
