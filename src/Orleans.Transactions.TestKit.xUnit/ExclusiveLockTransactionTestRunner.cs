using Xunit;
using Xunit.Abstractions;

namespace Orleans.Transactions.TestKit.xUnit
{
    public abstract class ExclusiveLockTransactionTestRunnerxUnit : ExclusiveLockTransactionTestRunner
    {
        protected ExclusiveLockTransactionTestRunnerxUnit(IGrainFactory grainFactory, ITestOutputHelper output)
            : base(grainFactory, output.WriteLine)
        {
        }

        [SkippableTheory]
        [InlineData(TransactionTestConstants.SingleStateTransactionalGrain)]
        public override Task ConcurrentReadThenWriteWithoutExclusiveLock_ThrowsLockException(string grainStates)
        {
            return base.ConcurrentReadThenWriteWithoutExclusiveLock_ThrowsLockException(grainStates);
        }

        [SkippableTheory]
        [InlineData(TransactionTestConstants.SingleStateTransactionalGrain)]
        public override Task ConcurrentReadThenWriteWithExclusiveLock_NoLockException(string grainStates)
        {
            return base.ConcurrentReadThenWriteWithExclusiveLock_NoLockException(grainStates);
        }
    }
}
