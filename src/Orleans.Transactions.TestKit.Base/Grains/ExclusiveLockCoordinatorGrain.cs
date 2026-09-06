using Orleans.Concurrency;

namespace Orleans.Transactions.TestKit
{
    /// <summary>
    /// Coordinates read-then-write transaction patterns used to verify exclusive locking behavior.
    /// </summary>
    [StatelessWorker]
    [Reentrant]
    public class ExclusiveLockCoordinatorGrain : Grain, IExclusiveLockCoordinatorGrain
    {
        /// <inheritdoc/>
        public async Task ReadThenWrite(ITransactionTestGrain grain, int value)
        {
            ArgumentNullException.ThrowIfNull(grain);
            await grain.Get();
            await Task.Delay(TimeSpan.FromMilliseconds(100)); // add some delay to make concurrent txs interleave each other
            await grain.Add(value);
        }

        /// <inheritdoc/>
        public async Task ReadThenWriteWithExclusiveLock(IExclusiveLockTransactionTestGrain grain, int value)
        {
            ArgumentNullException.ThrowIfNull(grain);
            await grain.Get();
            await Task.Delay(TimeSpan.FromMilliseconds(100)); // add some delay to make concurrent txs interleave each other
            await grain.Add(value);
        }
    }
}
