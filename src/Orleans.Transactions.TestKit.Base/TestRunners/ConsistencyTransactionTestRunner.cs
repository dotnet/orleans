using System;
using System.Threading.Tasks;
using AwesomeAssertions;
using Orleans.Transactions.TestKit.Consistency;

namespace Orleans.Transactions.TestKit
{
    /// <summary>
    /// Runs randomized transaction workloads and verifies that their recorded histories are consistent.
    /// </summary>
    public abstract class ConsistencyTransactionTestRunner : TransactionTestRunnerBase
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="ConsistencyTransactionTestRunner"/> class.
        /// </summary>
        /// <param name="grainFactory">The grain factory used to access test grains.</param>
        /// <param name="output">The callback used to write test output.</param>
        protected ConsistencyTransactionTestRunner(IGrainFactory grainFactory, Action<string> output)
        : base(grainFactory, output) { }


        // settings that are configuration dependent can be overridden by runner subclasses
        // this allows tests to adapt their logic, or be skipped, for specific contexts
        /// <summary>
        /// Gets a value indicating whether the storage adapter has limited space for commit records.
        /// </summary>
        protected abstract bool StorageAdaptorHasLimitedCommitSpace { get; }

        /// <summary>
        /// Gets a value indicating whether storage error injection is active.
        /// </summary>
        protected abstract bool StorageErrorInjectionActive { get; }

        /// <summary>
        /// Runs a deterministic randomized transaction workload and verifies the resulting history for consistency.
        /// </summary>
        /// <param name="numGrains">The number of grains participating in the workload.</param>
        /// <param name="scale">The workload scale, which determines the number of workers and transactions.</param>
        /// <param name="avoidDeadlocks">Whether the workload should avoid operations which can introduce deadlocks.</param>
        /// <param name="avoidTimeouts">Whether the workload should limit operations which can introduce response timeouts.</param>
        /// <param name="readwrite">The strategy used to determine whether transactions and grain calls are read-only or read-write.</param>
        /// <returns>A task which represents the consistency test.</returns>
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
