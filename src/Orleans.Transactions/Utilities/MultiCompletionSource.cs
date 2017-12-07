using System;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.Transactions
{
    /// <summary>
    /// Utility class that waits for the completion of multiple other tasks.
    /// Duplication of internal orleans utility MultiTaskCompletionSource.
    /// TODO: Consider making orleans version public. See Sharing non-core Code in https://github.com/dotnet/orleans/issues/3353
    /// </summary>
    internal class MultiCompletionSource
    {
        private readonly TaskCompletionSource<bool> tcs;
        private int count;

        public MultiCompletionSource(int count)
        {
            if (count <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(count), "count has to be positive.");
            }

            tcs = new TaskCompletionSource<bool>();
            this.count = count;
        }

        public Task Task => tcs.Task;

        public void SetException(Exception exception)
        {
            this.tcs.TrySetException(exception);
        }

        public void SetOneResult()
        {
            var newCount = Interlocked.Decrement(ref this.count);
            if (newCount < 0)
            {
                throw new InvalidOperationException(
                    nameof(this.SetOneResult) + " was called more times than initially specified by the count argument.");
            }

            if (newCount == 0)
            {
                tcs.TrySetResult(true);
            }
        }

        public void SetMultipleResults(int numResults)
        {
            if (numResults <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(numResults), "Value must be positive.");
            }

            var newCount = Interlocked.Add(ref this.count, -numResults);
            if (newCount < 0)
            {
                throw new InvalidOperationException(
                    nameof(this.SetMultipleResults) + " was called with a value greater than the remaining count.");
            }

            if (newCount == 0)
            {
                tcs.TrySetResult(true);
            }
        }
    }
}
