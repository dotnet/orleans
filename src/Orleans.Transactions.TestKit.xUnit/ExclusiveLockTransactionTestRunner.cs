using Xunit;

namespace Orleans.Transactions.TestKit.xUnit
{
    /// <inheritdoc cref="ExclusiveLockTransactionTestRunner"/>
    public abstract class ExclusiveLockTransactionTestRunnerxUnit : ExclusiveLockTransactionTestRunner
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="ExclusiveLockTransactionTestRunnerxUnit"/> class.
        /// </summary>
        /// <param name="grainFactory">The grain factory used to access test grains.</param>
        /// <param name="output">The xUnit test output helper.</param>
        protected ExclusiveLockTransactionTestRunnerxUnit(IGrainFactory grainFactory, ITestOutputHelper output)
            : base(grainFactory, output.WriteLine)
        {
        }

        /// <inheritdoc cref="ExclusiveLockTransactionTestRunner.ConcurrentReadThenWriteWithoutExclusiveLock_ThrowsLockException(string)"/>
        [Theory]
        [InlineData(TransactionTestConstants.SingleStateTransactionalGrain)]
        public override Task ConcurrentReadThenWriteWithoutExclusiveLock_ThrowsLockException(string grainStates)
        {
            return base.ConcurrentReadThenWriteWithoutExclusiveLock_ThrowsLockException(grainStates);
        }

        /// <inheritdoc cref="ExclusiveLockTransactionTestRunner.ConcurrentReadThenWriteWithExclusiveLock_NoLockException(string)"/>
        [Theory]
        [InlineData(TransactionTestConstants.SingleStateTransactionalGrain)]
        public override Task ConcurrentReadThenWriteWithExclusiveLock_NoLockException(string grainStates)
        {
            return base.ConcurrentReadThenWriteWithExclusiveLock_NoLockException(grainStates);
        }
    }
}
