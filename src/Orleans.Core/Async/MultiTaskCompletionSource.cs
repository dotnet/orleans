using System;
using System.Threading.Tasks;

namespace Orleans
{
    /// <summary>
    /// An alternative to <see cref="TaskCompletionSource{TResult}"/> which completes only once a specified number of signals have been received.
    /// </summary>
    internal class MultiTaskCompletionSource
    {
        private readonly TaskCompletionSource<bool> tcs;
        private int count;
        private readonly object lockable;

        /// <summary>
        /// Initializes a new instance of the <see cref="MultiTaskCompletionSource"/> class.
        /// </summary>
        /// <param name="count">
        /// The number of signals which must occur before this completion source completes.
        /// </param>
        /// <exception cref="ArgumentOutOfRangeException">
        /// The count value is less than or equal to zero.
        /// </exception>
        public MultiTaskCompletionSource(int count)
        {
            if (count <= 0)
            {
                throw new ArgumentOutOfRangeException("count", "count has to be positive.");
            }
            tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            this.count = count;
            lockable = new object();
        }

        /// <summary>
        /// Gets the task which is completed when a sufficient number of signals are received.
        /// </summary>
        public Task Task
        {
            get { return tcs.Task; }
        }

        /// <summary>
        /// Signals this instance.
        /// </summary>
        /// <exception cref="InvalidOperationException">This method was called more times than the initially specified count argument allows.</exception>
        public void SetOneResult()
        {
            lock (lockable)
            {
                if (count <= 0)
                {
                    throw new InvalidOperationException("SetOneResult was called more times than initially specified by the count argument.");
                }
                count--;
                if (count == 0)
                {
                    tcs.SetResult(true);
                }
            }
        }
    }
}
