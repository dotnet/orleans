using AwesomeAssertions;

namespace Orleans.Transactions.TestKit
{
    public abstract class ExclusiveLockTransactionTestRunner : TransactionTestRunnerBase
    {
        protected ExclusiveLockTransactionTestRunner(IGrainFactory grainFactory, Action<string> output)
        : base(grainFactory, output) { }

        /// <summary>
        /// Verifies that concurrent Read-then-Write transactions on the same grain
        /// cause OrleansTransactionLockUpgradeException or OrleansBrokenTransactionLockException
        /// when no exclusive lock is used.
        ///
        /// Scenario (LockUpgradeException):
        ///   TX1: PerformRead → shared lock acquired
        ///   TX2: PerformRead → shared lock acquired (same group)
        ///   TX1: PerformUpdate → lock upgrade fails
        ///
        /// Scenario (BrokenTransactionLockException):
        ///   TX1: PerformRead → shared lock acquired
        ///   TX2: PerformRead → shared lock acquired (same group)
        ///   TX2: PerformUpdate → upgraded, TX1 rolled back
        ///   TX1: PerformUpdate → broken lock detected
        /// </summary>
        public virtual async Task ConcurrentReadThenWriteWithoutExclusiveLock_ThrowsLockException(string grainStates)
        {
            const int concurrentTransactions = 10;
            const int addValue = 5;

            ITransactionTestGrain grain = RandomTestGrain(grainStates);

            var tasks = new List<Task>(concurrentTransactions);
            for (int i = 0; i < concurrentTransactions; i++)
            {
                var coordinator = this.grainFactory.GetGrain<IExclusiveLockCoordinatorGrain>(Guid.NewGuid());
                tasks.Add(coordinator.ReadThenWrite(grain, addValue));
            }

            var exceptions = new List<Exception>();
            foreach (var task in tasks)
            {
                try
                {
                    await task;
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            }

            this.testOutput($"Total exceptions: {exceptions.Count} out of {concurrentTransactions} transactions");
            foreach (var ex in exceptions)
            {
                this.testOutput($"  Exception type: {ex.GetType().Name}");
            }

            exceptions.Should().Contain(ex =>
                ex is OrleansTransactionLockUpgradeException
                || ex is OrleansBrokenTransactionLockException);

            var values = await grain.Get();
            values[0].Should().Be((concurrentTransactions - exceptions.Count) * addValue);
        }

        /// <summary>
        /// Verifies that using [UseExclusiveLock] on the read method prevents
        /// lock upgrade exceptions when concurrent transactions perform Read-then-Write on the same grain.
        /// </summary>
        public virtual async Task ConcurrentReadThenWriteWithExclusiveLock_NoLockException(string grainStates)
        {
            const int concurrentTransactions = 10;
            const int addValue = 5;

            var grain = RandomTestGrain<IExclusiveLockTransactionTestGrain>(
                TransactionTestConstants.ExclusiveLockTransactionTestGrain);

            var tasks = new List<Task>(concurrentTransactions);
            for (int i = 0; i < concurrentTransactions; i++)
            {
                var coordinator = this.grainFactory.GetGrain<IExclusiveLockCoordinatorGrain>(Guid.NewGuid());
                tasks.Add(coordinator.ReadThenWriteWithExclusiveLock(grain, addValue));
            }

            var lockExceptions = new List<Exception>();
            foreach (var task in tasks)
            {
                try
                {
                    await task;
                }
                catch (OrleansTransactionLockUpgradeException ex)
                {
                    lockExceptions.Add(ex);
                }
                catch (OrleansBrokenTransactionLockException ex)
                {
                    lockExceptions.Add(ex);
                }
            }

            this.testOutput($"Lock exceptions: {lockExceptions.Count} out of {concurrentTransactions} transactions");

            // No lock upgrade or broken lock exceptions should occur
            lockExceptions.Should().BeEmpty("UseExclusiveLock should prevent lock upgrade conflicts by acquiring exclusive locks from the start");

            // Verify the grain state is consistent - all successful transactions should have added their value
            int[] values = await grain.Get();
            this.testOutput($"Final grain value: {values[0]}");
            (values[0]).Should().Be(concurrentTransactions * addValue);
        }
    }
}
