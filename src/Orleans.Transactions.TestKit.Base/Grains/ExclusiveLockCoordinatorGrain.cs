using Orleans.Concurrency;

namespace Orleans.Transactions.TestKit
{
    [StatelessWorker]
    [Reentrant]
    public class ExclusiveLockCoordinatorGrain : Grain, IExclusiveLockCoordinatorGrain
    {
        public async Task ReadThenWrite(ITransactionTestGrain grain, int value)
        {
            await grain.Get();
            await Task.Delay(TimeSpan.FromMilliseconds(100)); // add some delay to make concurrent txs interleave each other
            await grain.Add(value);
        }

        public async Task ReadThenWriteWithExclusiveLock(IExclusiveLockTransactionTestGrain grain, int value)
        {
            await grain.Get();
            await Task.Delay(TimeSpan.FromMilliseconds(100)); // add some delay to make concurrent txs interleave each other
            await grain.Add(value);
        }
    }
}
